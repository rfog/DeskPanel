using System.Net.NetworkInformation;
using System.Net.Sockets;
using DeskPanel.Search.Searching;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeskPanel.Remote;

public sealed record WebServerOptions(
    bool LocalOnly,
    int Port,
    bool AllowDownloads,
    string PasswordHash,
    int MaxResults = 200,
    int MaxPreviewChars = 2_000_000,
    bool AllowActions = false);

public sealed record WebIndexInfo(string Name, int Documents, DateTime? UpdatedUtc);

/// <summary>A file that has been checked to live inside a published index's base folder.</summary>
public sealed record WebFileRef(string FullPath, string FileName);

/// <summary>
/// What the web layer needs from the application. Keeping it an interface keeps this project free of WPF and of the
/// configuration model, and lets the security behaviour be tested against a fake.
/// </summary>
public interface IWebBackend
{
    /// <summary>Only the indexes published to the web, never the whole configuration.</summary>
    IReadOnlyList<WebIndexInfo> GetIndexes();

    /// <param name="maxHits">How many hits to build snippets for; the cost of a search is mostly highlighting them.</param>
    Task<SearchResult> SearchAsync(string query, IReadOnlyCollection<string> indexNames, int maxHits, CancellationToken ct);

    /// <summary>
    /// Maps (index, relative path) to a real file, or null. Must refuse anything that is not a published index or that
    /// escapes the index's base folder.
    /// </summary>
    WebFileRef? ResolveFile(string indexName, string relativePath);

    Task<string> ReadTextAsync(string fullPath, CancellationToken ct);

    IReadOnlyList<SearchHistoryEntry> GetHistory();
    void RemoveHistory(string query);
    void ClearHistory();
}

/// <summary>
/// Read-only web interface to the search indexes, for phones and tablets on the home network (or through its VPN).
/// Plain HTTP on purpose for now: the intended use is a trusted LAN, with a VPN for anything outside it.
/// </summary>
public sealed class WebServer : IAsyncDisposable
{
    private const string CookieName = "dp_session";

    private readonly WebApplication _app;

    private WebServer(WebApplication app, IReadOnlyList<string> urls)
    {
        _app = app;
        Urls = urls;
    }

    /// <summary>Addresses the page can be reached at, for showing to the user.</summary>
    public IReadOnlyList<string> Urls { get; }

    public static async Task<WebServer> StartAsync(WebServerOptions options, IWebBackend backend, Action<string> log, IWebActions? actions = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "DeskPanel.Remote",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;

            // Every request body here is a tiny JSON object; anything bigger is not for us.
            kestrel.Limits.MaxRequestBodySize = 8 * 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            if (options.LocalOnly)
            {
                kestrel.ListenLocalhost(options.Port);
            }
            else
            {
                kestrel.ListenAnyIP(options.Port);
            }
        });
        builder.Services.AddResponseCompression();

        var app = builder.Build();
        Configure(app, options, backend, log, actions);

        await app.StartAsync();
        return new WebServer(app, BuildUrls(options));
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _app.StopAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Open connections are dropped; the process is not waiting on them.
        }

        await _app.DisposeAsync();
    }

    private static void Configure(WebApplication app, WebServerOptions options, IWebBackend backend, Action<string> log, IWebActions? actions)
    {
        // Actions exist only when the owner switched them on in Settings; otherwise every route below answers 403.
        var actionsEnabled = options.AllowActions && actions is not null;
        var sessions = new SessionStore();
        var throttle = new LoginThrottle();
        var limiter = new RateLimiter(perMinute: 600);
        var hitIds = new HitIds();
        var previewGate = new SemaphoreSlim(2);
        string Address(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

        // First in the pipeline so it covers everything after it. The client gets a generic message; the real
        // exception goes to the application log, never over the network.
        app.UseExceptionHandler(errors => errors.Run(async ctx =>
        {
            var error = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
            log($"Web: error en {ctx.Request.Method} {ctx.Request.Path}: {error?.Message}");
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsJsonAsync(new { error = "Error interno." });
        }));

        app.UseResponseCompression();

        // Headers first, so even error responses carry them. No inline script or style exists in the page, which is
        // what lets the policy be this strict.
        app.Use(async (ctx, next) =>
        {
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Content-Security-Policy"] =
                "default-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                headers["Cache-Control"] = "no-store";
            }

            await next();
        });

        app.Use(async (ctx, next) =>
        {
            if (!limiter.Allow(Address(ctx)))
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsJsonAsync(new { error = "Demasiadas peticiones; espera un minuto." });
                return;
            }

            // State-changing requests must carry a header a cross-site form or link cannot add (SameSite=Strict on
            // the cookie is the first defence, this is the second).
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
                && !ctx.Request.Headers.ContainsKey("X-DeskPanel"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { error = "Petición no válida." });
                return;
            }

            await next();
        });

        // Everything under /api needs a session, except logging in and asking whether there is one.
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            var isPublic = path.StartsWithSegments("/api/login") || path.StartsWithSegments("/api/session");
            if (path.StartsWithSegments("/api") && !isPublic && !sessions.Validate(ctx.Request.Cookies[CookieName]))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "Sesión no iniciada." });
                return;
            }

            await next();
        });

        var files = new EmbeddedFileProvider(typeof(WebServer).Assembly, "DeskPanel.Remote.wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = files,

            // The page is tiny and ships inside the exe; re-validating on every load beats serving a stale script.
            OnPrepareResponse = ctx => ctx.Context.Response.Headers["Cache-Control"] = "no-cache",
        });

        var api = app.MapGroup("/api");

        api.MapGet("/session", (HttpContext ctx) => Results.Json(new
        {
            authenticated = sessions.Validate(ctx.Request.Cookies[CookieName]),
            allowDownloads = options.AllowDownloads,
            maxResults = options.MaxResults,
            actions = actionsEnabled,
        }));

        api.MapPost("/login", async (HttpContext ctx, LoginRequest request) =>
        {
            var address = Address(ctx);
            if (throttle.IsLocked(address, out var wait))
            {
                ctx.Response.Headers.RetryAfter = ((int)wait.TotalSeconds + 1).ToString();
                log($"Web: acceso bloqueado temporalmente para {address}.");
                return Results.Json(new { error = $"Demasiados intentos. Espera {(int)Math.Ceiling(wait.TotalMinutes)} min." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 256
                || !PasswordHasher.Verify(request.Password, options.PasswordHash))
            {
                throttle.Failure(address);
                log($"Web: contraseña incorrecta desde {address}.");

                // A fixed pause on top of the lockout: guessing stays slow even before the lockout kicks in.
                await Task.Delay(500);
                return Results.Json(new { error = "Contraseña incorrecta." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            throttle.Success(address);
            ctx.Response.Cookies.Append(CookieName, sessions.Create(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = SessionStore.Lifetime,
                IsEssential = true,
            });
            log($"Web: sesión iniciada desde {address}.");
            return Results.Json(new { ok = true });
        });

        api.MapPost("/logout", (HttpContext ctx) =>
        {
            sessions.Remove(ctx.Request.Cookies[CookieName]);
            ctx.Response.Cookies.Delete(CookieName);
            return Results.Json(new { ok = true });
        });

        api.MapGet("/indexes", () => Results.Json(backend.GetIndexes().Select(i => new
        {
            name = i.Name,
            documents = i.Documents,
            updatedUtc = i.UpdatedUtc,
        })));

        api.MapGet("/search", async (HttpContext ctx, string? q) =>
        {
            q = q?.Trim();
            if (string.IsNullOrEmpty(q) || q.Length > 500)
            {
                return Results.Json(new { error = "Escribe qué buscar (máximo 500 caracteres)." }, statusCode: StatusCodes.Status400BadRequest);
            }

            // Only indexes that are published can be searched, whatever names the client asks for.
            var published = backend.GetIndexes().Select(i => i.Name).ToList();
            var asked = ctx.Request.Query["idx"].Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList();
            var selected = asked.Count == 0
                ? published
                : published.Where(p => asked.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();

            // A phone shows the first screenful quickly (50 hits); "Mostrar más" asks again for up to the configured maximum.
            var maxHits = int.TryParse(ctx.Request.Query["max"], out var asked_) ? Math.Clamp(asked_, 10, options.MaxResults) : Math.Min(50, options.MaxResults);

            var result = selected.Count == 0
                ? new SearchResult(Array.Empty<SearchHit>(), 0, Array.Empty<string>())
                : await backend.SearchAsync(q, selected, maxHits, ctx.RequestAborted);

            return Results.Json(new
            {
                total = result.TotalHits,
                terms = result.HighlightTerms,
                hits = result.Hits.Take(maxHits).Select(h => new
                {
                    id = hitIds.Create(h.IndexName, h.RelativePath),
                    index = h.IndexName,
                    name = h.FileName,
                    path = h.RelativePath,
                    ext = h.Extension,
                    score = h.Score,
                    snippet = h.Snippet,
                }),
            });
        });

        api.MapGet("/preview", async (HttpContext ctx, string? id) =>
        {
            if (!hitIds.TryParse(id, out var index, out var relative) || backend.ResolveFile(index, relative) is not { } file)
            {
                return Results.Json(new { error = "Fichero no disponible." }, statusCode: StatusCodes.Status404NotFound);
            }

            var terms = ctx.Request.Query["t"].Where(t => !string.IsNullOrEmpty(t) && t.Length <= 100).Select(t => t!).Take(50).ToList();

            // Extracting a big PDF is heavy; two at a time keeps the PC usable for its owner.
            await previewGate.WaitAsync(ctx.RequestAborted);
            try
            {
                var text = await backend.ReadTextAsync(file.FullPath, ctx.RequestAborted);
                var truncated = text.Length > options.MaxPreviewChars;
                if (truncated)
                {
                    text = text[..options.MaxPreviewChars];
                }

                var matches = await Task.Run(() => TermMatcher.FindAll(text, terms), ctx.RequestAborted);
                var flat = new int[matches.Count * 2];
                for (var i = 0; i < matches.Count; i++)
                {
                    flat[2 * i] = matches[i].Offset;
                    flat[2 * i + 1] = matches[i].Length;
                }

                return Results.Json(new { name = file.FileName, text, truncated, matches = flat });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"Web: no se pudo extraer el texto de {file.FileName}: {ex.Message}");
                return Results.Json(new { error = "No se pudo leer el contenido de este fichero." }, statusCode: StatusCodes.Status500InternalServerError);
            }
            finally
            {
                previewGate.Release();
            }
        });

        api.MapGet("/file", (HttpContext ctx, string? id) =>
        {
            if (!options.AllowDownloads)
            {
                return Results.Json(new { error = "Las descargas están desactivadas en este servidor." }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!hitIds.TryParse(id, out var index, out var relative) || backend.ResolveFile(index, relative) is not { } file)
            {
                return Results.Json(new { error = "Fichero no disponible." }, statusCode: StatusCodes.Status404NotFound);
            }

            // FileShare.ReadWrite|Delete: a file that is open elsewhere (or being synced) must still be downloadable.
            var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true);
            var contentType = new FileExtensionContentTypeProvider().TryGetContentType(file.FileName, out var known) ? known : "application/octet-stream";
            log($"Web: descarga de {file.FileName} desde {Address(ctx)}.");

            // Always an attachment: an HTML file from the archive must be saved, never rendered under this origin.
            return Results.File(stream, contentType, file.FileName, enableRangeProcessing: true);
        });

        api.MapGet("/history", () => Results.Json(backend.GetHistory().Select(h => new
        {
            query = h.Query,
            usedUtc = h.LastUsedUtc,
            uses = h.Uses,
        })));

        api.MapDelete("/history", (string? q) =>
        {
            if (!string.IsNullOrEmpty(q))
            {
                backend.RemoveHistory(q);
            }

            return Results.Json(new { ok = true });
        });

        api.MapPost("/history/clear", () =>
        {
            backend.ClearHistory();
            return Results.Json(new { ok = true });
        });

        ActionRoutes.Map(api, actionsEnabled, actions, log, Address);

    }

    private static IReadOnlyList<string> BuildUrls(WebServerOptions options)
    {
        if (options.LocalOnly)
        {
            return new[] { $"http://localhost:{options.Port}/" };
        }

        var urls = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                         .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                foreach (var address in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                {
                    urls.Add($"http://{address.Address}:{options.Port}/");
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Listing interfaces is only for display.
        }

        urls.Add($"http://{Environment.MachineName}:{options.Port}/");
        return urls;
    }
}

internal sealed record LoginRequest(string? Password);
