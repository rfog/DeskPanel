using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskPanel.Audio.Ai;

public sealed class LlmException : Exception
{
    public LlmException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

public interface ILlmClient
{
    string DisplayName { get; }

    /// <summary>Model ids the provider offers for text generation.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);

    Task<string> CompleteAsync(string system, string prompt, string model, double temperature, CancellationToken ct);

    /// <summary>Approximate characters of input one request may carry; drives how long transcripts are split.</summary>
    int MaxInputChars { get; }
}

/// <summary>Shared HTTP plumbing: one client, per-request timeouts, and errors that never echo credentials.</summary>
public abstract class LlmClientBase : ILlmClient
{
    // One HttpClient for the process (sockets are not released on dispose); per-request timeouts via CancellationToken.
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public abstract string DisplayName { get; }
    public abstract int MaxInputChars { get; }

    public abstract Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct);

    public abstract Task<string> CompleteAsync(string system, string prompt, string model, double temperature, CancellationToken ct);

    protected static async Task<JsonNode> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        try
        {
            using var response = await Http.SendAsync(request, timeoutSource.Token);
            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new LlmException($"HTTP {(int)response.StatusCode}: {ExtractError(body)}");
            }

            return JsonNode.Parse(body) ?? throw new LlmException("Respuesta vacía del servidor.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"Tiempo de espera agotado ({timeout.TotalSeconds:0} s).");
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"No se pudo conectar: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new LlmException("La respuesta del servidor no es JSON válido.", ex);
        }
    }

    protected static HttpRequestMessage Json(HttpMethod method, string url, JsonNode? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        return request;
    }

    // Providers put the useful part under error.message / error; a short excerpt is enough for the user.
    private static string ExtractError(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"]?["message"]?.GetValue<string>() ?? node?["error"]?.ToString() ?? node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message.Length > 400 ? message[..400] + "…" : message;
            }
        }
        catch (JsonException)
        {
            // Not JSON; fall through to the raw excerpt.
        }

        return body.Length > 300 ? body[..300] + "…" : body;
    }
}

/// <summary>LM Studio (and any OpenAI-compatible local server such as Ollama).</summary>
public sealed class LmStudioClient : LlmClientBase
{
    private readonly string _baseUrl;
    private readonly string? _token;

    public LmStudioClient(string baseUrl, string? token)
    {
        var url = baseUrl.Trim().TrimEnd('/');

        // People paste either http://host:1234 or http://host:1234/v1; the API lives under /v1.
        _baseUrl = url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url : url + "/v1";
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public override string DisplayName => "LM Studio";

    // Local models often run with 8-32k tokens of context; stay well inside it and let the splitter do the rest.
    public override int MaxInputChars => 24_000;

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var json = await SendAsync(Authorize(Json(HttpMethod.Get, _baseUrl + "/models")), TimeSpan.FromSeconds(20), ct);
        return json["data"]?.AsArray()
                   .Select(m => m?["id"]?.GetValue<string>())
                   .Where(id => !string.IsNullOrWhiteSpace(id) && !id!.Contains("embed", StringComparison.OrdinalIgnoreCase))
                   .Select(id => id!)
                   .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                   .ToList()
               ?? new List<string>();
    }

    public override async Task<string> CompleteAsync(string system, string prompt, string model, double temperature, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = temperature,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };

        // Loading a model on first use (JIT) can take minutes, and a long summary is generated at local speeds.
        var json = await SendAsync(Authorize(Json(HttpMethod.Post, _baseUrl + "/chat/completions", body)), TimeSpan.FromMinutes(30), ct);
        var text = json["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(text) ? throw new LlmException("El modelo no devolvió texto.") : text.Trim();
    }

    private HttpRequestMessage Authorize(HttpRequestMessage request)
    {
        if (_token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return request;
    }
}

public sealed class GeminiClient : LlmClientBase
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private readonly string _apiKey;
    private readonly string BaseUrl;

    /// <param name="baseUrl">Only for tests against a local mock; the real endpoint is the default.</param>
    public GeminiClient(string apiKey, string? baseUrl = null)
    {
        _apiKey = apiKey.Trim();
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public override string DisplayName => "Gemini";

    public override int MaxInputChars => 300_000;

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var models = new List<string>();
        string? pageToken = null;
        do
        {
            var url = $"{BaseUrl}/models?pageSize=1000" + (pageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(pageToken));
            var json = await SendAsync(WithKey(Json(HttpMethod.Get, url)), TimeSpan.FromSeconds(30), ct);
            foreach (var model in json["models"]?.AsArray() ?? new JsonArray())
            {
                var methods = model?["supportedGenerationMethods"]?.AsArray().Select(m => m?.GetValue<string>());
                var name = model?["name"]?.GetValue<string>();
                if (name is not null && methods is not null && methods.Contains("generateContent"))
                {
                    models.Add(name.StartsWith("models/") ? name["models/".Length..] : name);
                }
            }

            pageToken = json["nextPageToken"]?.GetValue<string>();
        }
        while (!string.IsNullOrEmpty(pageToken));

        return models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public override async Task<string> CompleteAsync(string system, string prompt, string model, double temperature, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }),
            }),
            ["generationConfig"] = new JsonObject { ["temperature"] = temperature },
        };

        var json = await SendAsync(WithKey(Json(HttpMethod.Post, $"{BaseUrl}/models/{Uri.EscapeDataString(model)}:generateContent", body)),
            TimeSpan.FromMinutes(10), ct);

        var parts = json["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        var text = parts is null ? null : string.Concat(parts.Select(p => p?["text"]?.GetValue<string>()));
        if (string.IsNullOrWhiteSpace(text))
        {
            var blocked = json["promptFeedback"]?["blockReason"]?.GetValue<string>();
            throw new LlmException(blocked is null ? "Gemini no devolvió texto." : $"Gemini bloqueó la petición ({blocked}).");
        }

        return text.Trim();
    }

    // Key in a header, not in the URL: URLs end up in logs and exception messages.
    private HttpRequestMessage WithKey(HttpRequestMessage request)
    {
        request.Headers.Add("x-goog-api-key", _apiKey);
        return request;
    }
}

public sealed class ClaudeClient : LlmClientBase
{
    private const string DefaultBaseUrl = "https://api.anthropic.com/v1";
    private readonly string _apiKey;
    private readonly string BaseUrl;

    /// <param name="baseUrl">Only for tests against a local mock; the real endpoint is the default.</param>
    public ClaudeClient(string apiKey, string? baseUrl = null)
    {
        _apiKey = apiKey.Trim();
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
    }

    public override string DisplayName => "Claude";

    public override int MaxInputChars => 300_000;

    public override async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct)
    {
        var json = await SendAsync(Authorize(Json(HttpMethod.Get, BaseUrl + "/models?limit=1000")), TimeSpan.FromSeconds(30), ct);
        return json["data"]?.AsArray()
                   .Select(m => m?["id"]?.GetValue<string>())
                   .Where(id => !string.IsNullOrWhiteSpace(id))
                   .Select(id => id!)
                   .ToList()
               ?? new List<string>();
    }

    public override async Task<string> CompleteAsync(string system, string prompt, string model, double temperature, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 8192,
            ["temperature"] = temperature,
            ["system"] = system,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
        };

        var json = await SendAsync(Authorize(Json(HttpMethod.Post, BaseUrl + "/messages", body)), TimeSpan.FromMinutes(10), ct);
        var blocks = json["content"]?.AsArray();
        var text = blocks is null
            ? null
            : string.Concat(blocks.Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b?["text"]?.GetValue<string>()));
        return string.IsNullOrWhiteSpace(text) ? throw new LlmException("Claude no devolvió texto.") : text.Trim();
    }

    private HttpRequestMessage Authorize(HttpRequestMessage request)
    {
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }
}

public static class LlmClientFactory
{
    public const string LmStudio = "LmStudio";
    public const string Gemini = "Gemini";
    public const string Claude = "Claude";

    public static ILlmClient Create(string provider, string? lmStudioUrl, string? apiKeyOrToken) => provider switch
    {
        LmStudio => string.IsNullOrWhiteSpace(lmStudioUrl)
            ? throw new LlmException("Falta la URL de LM Studio.")
            : new LmStudioClient(lmStudioUrl, apiKeyOrToken),
        Gemini => string.IsNullOrWhiteSpace(apiKeyOrToken) ? throw new LlmException("Falta la clave de API de Gemini.") : new GeminiClient(apiKeyOrToken),
        Claude => string.IsNullOrWhiteSpace(apiKeyOrToken) ? throw new LlmException("Falta la clave de API de Claude.") : new ClaudeClient(apiKeyOrToken),
        _ => throw new LlmException($"Proveedor desconocido: {provider}"),
    };
}
