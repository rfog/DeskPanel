using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DeskPanel.Remote;

internal sealed record AudioStartRequest(List<string>? Names);

internal sealed record EntryRequest(string? Id, string? Profile);

/// <summary>The routes that make things happen on the PC. All of them sit behind the session check and the "actions" switch.</summary>
internal static partial class ActionRoutes
{
    [GeneratedRegex("^[0-9a-f]{8,64}$")]
    private static partial Regex IdPattern();

    public static void Map(RouteGroupBuilder api, bool enabled, IWebActions? actions, Action<string> log, Func<HttpContext, string> address)
    {
        var group = api.MapGroup("/actions");

        // One place decides: with the switch off none of these routes does anything, whatever the client sends.
        group.AddEndpointFilter(async (context, next) =>
        {
            if (!enabled || actions is null)
            {
                return Results.Json(new { error = "Las acciones están desactivadas en este servidor (Configurar › Web)." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            return await next(context);
        });

        group.MapGet("/audio", async () => Results.Json(await actions!.GetAudioAsync()));

        group.MapPost("/audio/start", async (HttpContext ctx, AudioStartRequest request) =>
        {
            var names = (request.Names ?? new List<string>()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count == 0 || names.Count > 500 || names.Any(n => n.Length > 260))
            {
                return Results.Json(new { error = "Marca al menos un audio." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var problem = await actions!.StartAudioAsync(names);
            if (problem is not null)
            {
                return Results.Json(new { error = problem }, statusCode: StatusCodes.Status409Conflict);
            }

            log($"Web: proceso de {names.Count} audio(s) iniciado desde {address(ctx)}.");
            return Results.Json(new { ok = true });
        });

        group.MapPost("/audio/cancel", async (HttpContext ctx) =>
        {
            await actions!.CancelAudioAsync();
            log($"Web: proceso de audio cancelado desde {address(ctx)}.");
            return Results.Json(new { ok = true });
        });

        group.MapGet("/feeds", async (HttpContext ctx, bool? refresh, bool? read) =>
            Results.Json(await actions!.GetFeedsAsync(refresh == true, read == true, ctx.RequestAborted)));

        group.MapGet("/feeds/preview", async (HttpContext ctx, string? id, string? profile) =>
        {
            if (!ValidId(id) || ValidProfile(profile) is false)
            {
                return Results.Json(new { error = "Entrada no válida." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var html = await actions!.GetFeedPreviewAsync(id!, profile, ctx.RequestAborted);
            if (html is null)
            {
                return Results.Json(new { error = "La entrada ya no está disponible. Actualiza la lista." }, statusCode: StatusCodes.Status404NotFound);
            }

            // Third-party markup: it is shown only inside a sandboxed frame, and this policy gives that document no script at
            // all. Frames are allowed for this one response only (the rest of the site refuses to be framed). The frame
            // element itself carries "allow-same-origin" (and no allow-scripts): without it the frame's request goes out from
            // an opaque origin, the SameSite=Strict session cookie is not sent, and the frame gets a 401 it may not show.
            var headers = ctx.Response.Headers;

            // frame-ancestors replaces the site-wide X-Frame-Options: DENY for this response only.
            headers.Remove("X-Frame-Options");
            headers["Content-Security-Policy"] =
                "default-src 'none'; img-src https: http: data:; style-src 'unsafe-inline'; font-src https: data:; " +
                "media-src https: http:; sandbox allow-popups allow-popups-to-escape-sandbox; frame-ancestors 'self'";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        group.MapPost("/feeds/pdf", async (HttpContext ctx, EntryRequest request) =>
        {
            if (!ValidId(request.Id) || ValidProfile(request.Profile) is false)
            {
                return Results.Json(new { error = "Entrada no válida." }, statusCode: StatusCodes.Status400BadRequest);
            }

            var result = await actions!.GeneratePdfAsync(request.Id!, request.Profile, ctx.RequestAborted);
            log($"Web: conversión a PDF pedida desde {address(ctx)}: {(result.Ok ? "correcta" : "fallida")}.");
            return Results.Json(result);
        });

        group.MapPost("/feeds/full", async (HttpContext ctx, EntryRequest request) =>
            ValidId(request.Id)
                ? Results.Json(await actions!.ToggleFullContentAsync(request.Id!, ctx.RequestAborted))
                : Results.Json(new { error = "Entrada no válida." }, statusCode: StatusCodes.Status400BadRequest));

        group.MapPost("/feeds/summary", async (HttpContext ctx, EntryRequest request) =>
            ValidId(request.Id)
                ? Results.Json(await actions!.SummarizeAsync(request.Id!, ctx.RequestAborted))
                : Results.Json(new { error = "Entrada no válida." }, statusCode: StatusCodes.Status400BadRequest));

        group.MapPost("/feeds/summary/save", async (HttpContext ctx, EntryRequest request) =>
            ValidId(request.Id)
                ? Results.Json(await actions!.SaveSummaryAsync(request.Id!, ctx.RequestAborted))
                : Results.Json(new { error = "Entrada no válida." }, statusCode: StatusCodes.Status400BadRequest));
    }

    private static bool ValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    // A profile is only a name to look up in the configuration; bound it so nothing odd travels further.
    private static bool? ValidProfile(string? profile) => profile is null ? null : profile.Length <= 80 && !profile.Any(char.IsControl);
}
