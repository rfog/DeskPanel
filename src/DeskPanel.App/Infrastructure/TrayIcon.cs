using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace DeskPanel.App.Infrastructure;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Shell shell)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Mostrar panel", null, (_, _) => shell.ShowPanel());
        menu.Items.Add("Buscar en índices", null, (_, _) => shell.OpenSearch());
        menu.Items.Add("Feeds a PDF", null, (_, _) => shell.OpenFeeds());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Editar configuración", null, (_, _) => shell.EditConfiguration());
        menu.Items.Add("Recargar configuración", null, (_, _) => shell.ReloadConfiguration());
        menu.Items.Add("Abrir carpeta de configuración", null, (_, _) => shell.OpenFolder(shell.Config.ConfigDirectory));
        menu.Items.Add("Abrir registro", null, (_, _) => shell.OpenLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _icon = new NotifyIcon
        {
            // Placeholder until the project gets its own .ico; the exe's associated icon is at least recognisable.
            Icon = LoadIcon(),
            Text = "DeskPanel",
            ContextMenuStrip = menu,
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                shell.TogglePanel();
            }
        };
    }

    public void Show() => _icon.Visible = true;

    public void Notify(string title, string text, bool isWarning = false) =>
        _icon.ShowBalloonTip(6000, title, text, isWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    public void Dispose()
    {
        // Hiding before disposal prevents the "ghost" icon that lingers until the mouse passes over the tray.
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static Icon LoadIcon()
    {
        // Ask the .ico for the tray's own size (16 px at 100% scale): ExtractAssociatedIcon returns the 32 px image,
        // which the tray then shrinks and blurs.
        var file = Path.Combine(AppContext.BaseDirectory, "assets", "icons", "deskpanel.ico");
        if (File.Exists(file))
        {
            try
            {
                return new Icon(file, SystemInformation.SmallIconSize);
            }
            catch (ArgumentException)
            {
                // Fall through to the exe's embedded icon.
            }
        }

        var exe = Environment.ProcessPath;
        return (exe is null ? null : Icon.ExtractAssociatedIcon(exe)) ?? SystemIcons.Application;
    }
}
