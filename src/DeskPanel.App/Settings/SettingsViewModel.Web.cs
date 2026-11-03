using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPanel.App.Infrastructure;
using DeskPanel.App.Web;
using DeskPanel.Core.Configuration;
using DeskPanel.Core.Diagnostics;
using DeskPanel.Remote;

namespace DeskPanel.App.Settings;

/// <summary>One index that can be ticked to publish it on the web.</summary>
internal sealed partial class WebIndexChoice : ObservableObject
{
    [ObservableProperty]
    private bool _isPublished;

    public WebIndexChoice(string name, bool isPublished)
    {
        Name = name;
        _isPublished = isPublished;
    }

    public string Name { get; }
}

/// <summary>The "Web" tab. Everything here is stored in machine.json: serving is a per-machine decision.</summary>
internal sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private bool _webEnabled;

    /// <summary>0 = local network, 1 = this PC only. One value for one ComboBox: the two coupled radio buttons it replaces
    /// did not reliably keep the user's choice.</summary>
    [ObservableProperty]
    private int _webScopeIndex;

    [ObservableProperty]
    private string _webRemoteAddress = "";

    [ObservableProperty]
    private string _webPort = "8787";

    [ObservableProperty]
    private bool _webAllowDownloads = true;

    [ObservableProperty]
    private bool _webAllowActions;

    [ObservableProperty]
    private string _webPassword = "";

    [ObservableProperty]
    private string _webPasswordInfo = "";

    [ObservableProperty]
    private string _webStatus = "";

    public ObservableCollection<WebIndexChoice> WebIndexChoices { get; } = new();

    // The fields are assigned directly on purpose (see LoadAi): no change callbacks while loading.
#pragma warning disable MVVMTK0034
    private void LoadWeb(MachineConfig machine, AppConfig app)
    {
        var web = machine.Web;
        _webEnabled = web.Enabled;
        _webScopeIndex = web.Scope == WebServerConfig.ScopeLocal ? 1 : 0;
        _webRemoteAddress = web.RemoteAddress ?? "";
        _webPort = web.Port.ToString();
        _webAllowDownloads = web.AllowDownloads;
        _webAllowActions = web.AllowActions;
        _webPasswordInfo = HasWebPassword() ? "Hay una contraseña guardada." : "Sin contraseña: el servidor no arrancará hasta que la establezcas.";
        _webStatus = _shell.Web.Status;

        foreach (var definition in app.Search.Indexes)
        {
            WebIndexChoices.Add(new WebIndexChoice(definition.Name, web.PublishedIndexes.Contains(definition.Name, StringComparer.OrdinalIgnoreCase)));
        }
    }
#pragma warning restore MVVMTK0034

    private static bool HasWebPassword() => !string.IsNullOrEmpty(SecretStore.Get(WebServerManager.PasswordSecretName));

    /// <summary>The first problem with the web settings, or null.</summary>
    private string? ValidateWeb()
    {
        if (!int.TryParse(WebPort.Trim(), out var port) || port is < 1024 or > 65535)
        {
            return "El puerto del servidor web debe ser un número entre 1024 y 65535.";
        }

        if (!string.IsNullOrWhiteSpace(WebRemoteAddress) && WebServerConfig.BuildRemoteUrl(WebRemoteAddress, port) is null)
        {
            return "La dirección del servidor remoto no es válida. Usa una IP o nombre (192.168.1.200), «ip:puerto» o http://…";
        }

        if (!string.IsNullOrEmpty(WebPassword) && PasswordHasher.Validate(WebPassword) is { } weak)
        {
            return weak;
        }

        if (WebEnabled && string.IsNullOrEmpty(WebPassword) && !HasWebPassword())
        {
            return "Para activar el servidor web hay que establecer una contraseña (mínimo 10 caracteres).";
        }

        return null;
    }

    private void ApplyWeb(MachineConfig machine)
    {
        var web = machine.Web;
        web.Enabled = WebEnabled;
        web.Scope = WebScopeIndex == 0 ? WebServerConfig.ScopeLan : WebServerConfig.ScopeLocal;
        web.RemoteAddress = string.IsNullOrWhiteSpace(WebRemoteAddress) ? null : WebRemoteAddress.Trim();
        AppLog.Info($"Web settings saved: enabled={web.Enabled}, scope={web.Scope}, port={web.Port}.");
        web.Port = int.Parse(WebPort.Trim());
        web.AllowDownloads = WebAllowDownloads;
        web.AllowActions = WebAllowActions;
        web.PublishedIndexes = WebIndexChoices.Where(c => c.IsPublished).Select(c => c.Name).ToList();

        // Only the hash is kept, in the DPAPI-protected store; an empty box means "keep the current password".
        if (!string.IsNullOrEmpty(WebPassword))
        {
            SecretStore.Set(WebServerManager.PasswordSecretName, PasswordHasher.Hash(WebPassword));
        }
    }

    [RelayCommand]
    private void RefreshWebStatus() => WebStatus = _shell.Web.Status;

    [RelayCommand]
    private void OpenWebPage()
    {
        if (!_shell.Web.IsRunning)
        {
            WebStatus = "El servidor no está en marcha. Guarda la configuración con «Activar» marcado.";
            return;
        }

        var port = int.TryParse(WebPort.Trim(), out var p) ? p : 8787;
        _shell.OpenWithShell($"http://localhost:{port}/");
    }

    [RelayCommand]
    private void CopyWebAddress()
    {
        var address = _shell.Web.Urls.FirstOrDefault(u => !u.Contains("localhost")) ?? _shell.Web.Urls.FirstOrDefault();
        if (address is null)
        {
            WebStatus = "El servidor no está en marcha.";
            return;
        }

        try
        {
            Clipboard.SetText(address);
            WebStatus = $"Copiada: {address}";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program holds the clipboard for a moment; the address is on screen anyway.
            WebStatus = $"No se pudo copiar. Dirección: {address}";
        }
    }
}
