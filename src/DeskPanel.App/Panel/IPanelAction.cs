using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DeskPanel.App.Panel;

/// <summary>
/// A button on the panel. New features plug in here without touching the panel layout.
/// </summary>
internal interface IPanelAction
{
    string Title { get; }

    /// <summary>Segoe Fluent Icons / MDL2 code point.</summary>
    string Glyph { get; }

    string ToolTip { get; }
    bool IsEnabled { get; }
    Task ExecuteAsync();
}

/// <summary>An action that also accepts files dropped on its button (e.g. audio onto "Audio").</summary>
internal interface IFileDropAction : IPanelAction
{
    /// <summary>False for actions that do not take files at all, so their buttons are not drop targets.</summary>
    bool AcceptsFiles { get; }

    bool CanAccept(IReadOnlyList<string> paths);
    Task OnFilesDroppedAsync(IReadOnlyList<string> paths);
}

internal sealed class DelegatePanelAction : IFileDropAction
{
    private readonly Func<Task> _execute;
    private readonly Func<IReadOnlyList<string>, bool>? _accepts;
    private readonly Func<IReadOnlyList<string>, Task>? _onDrop;

    public DelegatePanelAction(string title, string glyph, string toolTip, Action execute, bool isEnabled = true,
        Func<IReadOnlyList<string>, bool>? accepts = null, Func<IReadOnlyList<string>, Task>? onDrop = null)
        : this(title, glyph, toolTip, () => { execute(); return Task.CompletedTask; }, isEnabled, accepts, onDrop)
    {
    }

    public DelegatePanelAction(string title, string glyph, string toolTip, Func<Task> execute, bool isEnabled = true,
        Func<IReadOnlyList<string>, bool>? accepts = null, Func<IReadOnlyList<string>, Task>? onDrop = null)
    {
        Title = title;
        Glyph = glyph;
        ToolTip = toolTip;
        IsEnabled = isEnabled;
        _execute = execute;
        _accepts = accepts;
        _onDrop = onDrop;
    }

    public string Title { get; }
    public string Glyph { get; }
    public string ToolTip { get; }
    public bool IsEnabled { get; }

    public Task ExecuteAsync() => _execute();

    public bool AcceptsFiles => _accepts is not null;

    public bool CanAccept(IReadOnlyList<string> paths) => IsEnabled && _accepts?.Invoke(paths) == true;

    public Task OnFilesDroppedAsync(IReadOnlyList<string> paths) => _onDrop?.Invoke(paths) ?? Task.CompletedTask;
}
