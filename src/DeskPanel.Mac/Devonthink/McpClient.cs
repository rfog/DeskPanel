using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskPanel.Core.Diagnostics;

namespace DeskPanel.Mac.Devonthink;

public sealed class McpException : Exception
{
    public McpException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>One way of exchanging JSON-RPC messages with an MCP server.</summary>
public interface IMcpTransport : IAsyncDisposable
{
    /// <summary>Sends a request and returns its JSON-RPC response, or null for a notification (no id).</summary>
    Task<JsonNode?> SendAsync(JsonObject message, CancellationToken ct);
}

/// <summary>
/// A minimal MCP client: initialise, then call tools. It is used per operation (connect, call, dispose), so there is
/// no session state to keep alive between searches.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private const string ProtocolVersion = "2025-03-26";

    private readonly IMcpTransport _transport;
    private int _nextId;

    private McpClient(IMcpTransport transport) => _transport = transport;

    public static async Task<McpClient> ConnectAsync(IMcpTransport transport, CancellationToken ct)
    {
        var client = new McpClient(transport);
        try
        {
            await client.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "DeskPanel", ["version"] = "0.1" },
            }, ct);
            await transport.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, ct);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    /// <summary>Calls a tool and returns the text of its answer (DEVONthink answers with one JSON document as text).</summary>
    public async Task<string> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, ct);
        var text = string.Concat((result?["content"] as JsonArray ?? new JsonArray())
            .Select(part => part?["type"]?.GetValue<string>() == "text" ? part["text"]?.GetValue<string>() : null)
            .Where(t => t is not null));
        if (result?["isError"]?.GetValue<bool>() == true)
        {
            throw new McpException($"DEVONthink devolvió un error en «{name}»: {text}");
        }

        return text;
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private async Task<JsonNode?> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var response = await _transport.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters,
        }, ct) ?? throw new McpException($"El servidor MCP no respondió a «{method}».");

        if (response["error"] is { } error)
        {
            throw new McpException($"Error MCP en «{method}»: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}");
        }

        return response["result"];
    }
}

/// <summary>MCP over HTTP (JSON-RPC in POST bodies; the answer is plain JSON or a one-event stream) with a bearer token.</summary>
public sealed class HttpMcpTransport : IMcpTransport
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly Uri _endpoint;
    private readonly string? _token;
    private string? _sessionId;

    public HttpMcpTransport(int port, string? token)
    {
        _endpoint = new Uri($"http://localhost:{port}/mcp");
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public async Task<JsonNode?> SendAsync(JsonObject message, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (_token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        if (_sessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", _sessionId);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new McpException($"No se pudo conectar con el servidor MCP en {_endpoint} (¿DEVONthink está abierto y el servidor activo?): {ex.Message}", ex);
        }

        using (response)
        {
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var session))
            {
                _sessionId = session.FirstOrDefault();
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                throw new McpException("El servidor MCP rechazó el token (401). Comprueba el token y el puerto en Ajustes › DEVONthink.");
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);
            if (message["id"] is null || string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                // Each event carries one message in its data: lines; the one with our id is the answer.
                foreach (var data in body.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()))
                {
                    if (JsonNode.Parse(data) is { } node && node["id"] is not null)
                    {
                        return node;
                    }
                }

                return null;
            }

            return JsonNode.Parse(body);
        }
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>MCP over a child process's stdin/stdout, one JSON message per line. Needs no port and no token.</summary>
public sealed class StdioMcpTransport : IMcpTransport
{
    public const string DefaultCommand = "/Applications/DEVONthink.app/Contents/Library/LoginItems/DEVONthink MCP.app/Contents/MacOS/DEVONthink MCP";

    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromMinutes(5);

    private readonly Process _process;
    private readonly Dictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    private readonly object _gate = new();

    public StdioMcpTransport(string? command)
    {
        var path = string.IsNullOrWhiteSpace(command) ? DefaultCommand : command;
        if (!File.Exists(path))
        {
            throw new McpException($"No se encuentra el ayudante MCP de DEVONthink en '{path}'.");
        }

        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--stdio");
        _process = Process.Start(info) ?? throw new McpException("No se pudo iniciar el ayudante MCP de DEVONthink.");

        // The helper logs to stderr; an unread pipe would eventually block it.
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        _ = Task.Run(ReadLoopAsync);
    }

    public async Task<JsonNode?> SendAsync(JsonObject message, CancellationToken ct)
    {
        TaskCompletionSource<JsonNode>? completion = null;
        if (message["id"]?.GetValue<int>() is { } id)
        {
            completion = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _pending[id] = completion;
            }
        }

        await _process.StandardInput.WriteLineAsync(message.ToJsonString().AsMemory(), ct);
        await _process.StandardInput.FlushAsync(ct);
        return completion is null ? null : await completion.Task.WaitAsync(ReplyTimeout, ct);
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(2_000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            AppLog.Warn("Could not stop the DEVONthink MCP helper cleanly.", ex);
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync()) is not null)
            {
                // The helper may print banners to stdout too; only JSON objects are protocol messages.
                if (!line.StartsWith('{'))
                {
                    continue;
                }

                if (JsonNode.Parse(line) is { } node && node["id"]?.GetValue<int>() is { } id)
                {
                    TaskCompletionSource<JsonNode>? completion;
                    lock (_gate)
                    {
                        _pending.Remove(id, out completion);
                    }

                    completion?.TrySetResult(node);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or ObjectDisposedException)
        {
            // Falls through to failing whatever is still waiting.
        }

        lock (_gate)
        {
            foreach (var completion in _pending.Values)
            {
                completion.TrySetException(new McpException("El ayudante MCP de DEVONthink terminó antes de responder."));
            }

            _pending.Clear();
        }
    }
}
