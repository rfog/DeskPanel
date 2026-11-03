using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Remote;

namespace DeskPanel.App.Web;

/// <summary>
/// Owns the web server's life: starts it from <see cref="WebServerConfig"/> and the stored password, and restarts it
/// whenever the configuration is reloaded (a restart also signs every browser out, which is what a changed password needs).
/// </summary>
internal sealed class WebServerManager : IDisposable
{
    /// <summary>Name of the password hash in <see cref="DeskPanel.App.Infrastructure.SecretStore"/>.</summary>
    public const string PasswordSecretName = "web-password-hash";

    private readonly Shell _shell;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebServer? _server;

    public WebServerManager(Shell shell) => _shell = shell;

    public string Status { get; private set; } = "Desactivado.";

    public IReadOnlyList<string> Urls { get; private set; } = Array.Empty<string>();

    public bool IsRunning => _server is not null;

    public event Action? StatusChanged;

    /// <summary>Stops the running server, if any, and starts a new one when the configuration says so.</summary>
    public async Task ApplyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();

            var web = _shell.Config.Machine.Web;
            if (!web.Enabled)
            {
                Update("Desactivado.");
                return;
            }

            var hash = Infrastructure.SecretStore.Get(PasswordSecretName);
            if (string.IsNullOrEmpty(hash))
            {
                Update("No arranca: falta la contraseña (Configurar › Web).");
                return;
            }

            var options = new WebServerOptions(
                LocalOnly: web.Scope != WebServerConfig.ScopeLan,
                Port: web.Port,
                AllowDownloads: web.AllowDownloads,
                PasswordHash: hash,
                MaxResults: Math.Max(1, _shell.Config.App.Search.MaxResults),
                AllowActions: web.AllowActions);

            _server = await WebServer.StartAsync(options, new WebBackend(_shell), message => AppLog.Info(message), new WebActions(_shell));
            Urls = _server.Urls;
            AppLog.Info($"Web server listening on {string.Join(", ", Urls)}");
            Update("En marcha:  " + string.Join("   ·   ", Urls));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                                       or System.Net.Sockets.SocketException)
        {
            // Most often the port is already taken (AddressInUse is an IOException).
            AppLog.Error("Web server failed to start.", ex);
            Update($"No se pudo iniciar: {ex.Message}");
            _shell.Notify("DeskPanel", $"El servidor web no ha podido arrancar: {ex.Message}", isWarning: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        // Called at exit; do not hang the shutdown on a stuck connection.
        try
        {
            StopCoreAsync().Wait(TimeSpan.FromSeconds(6));
        }
        catch (AggregateException)
        {
            // Nothing useful to do about a failed stop while the process is ending.
        }
    }

    private async Task StopCoreAsync()
    {
        var server = _server;
        _server = null;
        Urls = Array.Empty<string>();
        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }

    private void Update(string status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }
}
