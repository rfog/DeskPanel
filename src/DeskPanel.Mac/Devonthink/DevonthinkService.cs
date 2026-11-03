using System.Text.Json;
using System.Text.Json.Nodes;
using DeskPanel.Core.Configuration;
using DeskPanel.Mac.Services;

namespace DeskPanel.Mac.Devonthink;

/// <param name="Location">The group path inside the database, e.g. "/Propias/RFOG's Starred/".</param>
public sealed record DevonthinkHit(string Uuid, string Name, string DatabaseName, string DatabaseUuid, string Location, string Kind, double Score)
{
    /// <summary>The link DEVONthink resolves to the record, opened with <c>open</c>.</summary>
    public string ItemUrl => $"x-devonthink-item://{Uuid}";
}

public sealed record DevonthinkSearchResult(IReadOnlyList<DevonthinkHit> Hits, int Total);

/// <summary>Reads the open databases and searches them through DEVONthink's MCP server (read-only tools only).</summary>
public sealed class DevonthinkService
{
    private readonly MacServices _services;

    public DevonthinkService(MacServices services) => _services = services;

    /// <summary>Connects with explicit values so Settings can try what is typed before saving it.</summary>
    public static async Task<McpClient> ConnectAsync(string transport, int port, string? token, string? stdioCommand, CancellationToken ct)
    {
        IMcpTransport mcp = transport == DevonthinkConfig.TransportStdio
            ? new StdioMcpTransport(stdioCommand)
            : new HttpMcpTransport(port, token);
        try
        {
            return await McpClient.ConnectAsync(mcp, ct);
        }
        catch
        {
            await mcp.DisposeAsync();
            throw;
        }
    }

    public Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        var config = _services.Config.App.Devonthink;
        return ConnectAsync(config.Transport, config.Port, _services.Secrets.Get(SecretNames.DevonthinkMcpToken), config.StdioCommand, ct);
    }

    public static async Task<List<DevonthinkDatabase>> ReadDatabasesAsync(McpClient client, CancellationToken ct)
    {
        var text = await client.CallToolAsync("get_databases", new JsonObject(), ct);
        var databases = new List<DevonthinkDatabase>();
        foreach (var entry in JsonNode.Parse(text)?.AsArray() ?? new JsonArray())
        {
            if (entry?["uuid"]?.GetValue<string>() is { Length: > 0 } uuid)
            {
                databases.Add(new DevonthinkDatabase { Name = entry["name"]?.GetValue<string>() ?? uuid, Uuid = uuid });
            }
        }

        return databases.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Reads the databases DEVONthink has open now and stores them in the configuration.</summary>
    public async Task<IReadOnlyList<DevonthinkDatabase>> RefreshDatabasesAsync(CancellationToken ct)
    {
        await using var client = await ConnectAsync(ct);
        var databases = await ReadDatabasesAsync(client, ct);
        _services.Config.App.Devonthink.Databases = databases;
        _services.Config.Save();
        return databases;
    }

    /// <param name="databaseUuids">The databases to search; null or empty means all that are open.</param>
    public async Task<DevonthinkSearchResult> SearchAsync(string query, IReadOnlyCollection<string>? databaseUuids, CancellationToken ct)
    {
        var limit = Math.Clamp(_services.Config.App.Devonthink.MaxResults, 1, 1000);
        await using var client = await ConnectAsync(ct);

        // The tool searches one database or all of them, so a subset means one call per database, merged by score.
        var scopes = databaseUuids is { Count: > 0 } ? databaseUuids.Cast<string?>().ToList() : new List<string?> { null };
        var hits = new List<DevonthinkHit>();
        var total = 0;
        foreach (var scope in scopes)
        {
            var arguments = new JsonObject { ["query"] = query, ["limit"] = limit };
            if (scope is not null)
            {
                arguments["database_uuid"] = scope;
            }

            var text = await client.CallToolAsync("search_records", arguments, ct);
            var root = JsonNode.Parse(text) ?? throw new McpException("Respuesta vacía de search_records.");
            total += root["total"]?.GetValue<int>() ?? 0;
            foreach (var item in root["results"]?.AsArray() ?? new JsonArray())
            {
                if (item?["uuid"]?.GetValue<string>() is not { Length: > 0 } uuid)
                {
                    continue;
                }

                hits.Add(new DevonthinkHit(
                    uuid,
                    item["name"]?.GetValue<string>() ?? "",
                    item["databaseName"]?.GetValue<string>() ?? "",
                    item["databaseUUID"]?.GetValue<string>() ?? "",
                    item["location"]?.GetValue<string>() ?? "",
                    item["kind"]?.GetValue<string>() ?? item["type"]?.GetValue<string>() ?? "",
                    item["score"]?.GetValue<double>() ?? 0));
            }
        }

        var merged = scopes.Count > 1 ? hits.OrderByDescending(h => h.Score).Take(limit).ToList() : hits;
        return new DevonthinkSearchResult(merged, total);
    }
}
