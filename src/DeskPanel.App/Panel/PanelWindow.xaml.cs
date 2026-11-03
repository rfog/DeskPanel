using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using DeskPanel.App.Panel;

namespace DeskPanel.App.Panel;

internal partial class PanelWindow : Window
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToNearest = 2;

    public PanelWindow(PanelViewModel viewModel)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // Launcher behaviour: clicking anywhere else dismisses the panel.
        Deactivated += (_, _) =>
        {
            if (!ViewModel.KeepOpenOnFocusLoss)
            {
                Hide();
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        FolderList.MouseDoubleClick += (_, _) => OpenSelectedFolder();
    }

    public PanelViewModel ViewModel { get; }

    /// <summary>
    /// Positions the panel on the monitor under the cursor. Placement is done in physical pixels through
    /// SetWindowPos because WPF's DIP coordinates depend on the DPI of the monitor the window is currently on,
    /// which is wrong when it is about to appear on a different one.
    /// </summary>
    public void ShowNearCursor()
    {
        ViewModel.FolderFilter = "";
        ViewModel.Refresh();

        // Always above other windows: when summoned mid-drag from Explorer it has to be visible to drop onto, and
        // when pinned it must not sink behind the windows it launches.
        Topmost = true;

        var cursor = System.Windows.Forms.Cursor.Position;
        var area = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var scale = GetMonitorScale(cursor.X, cursor.Y);

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var widthPx = (int)(Width * scale);
        var x = area.Left + (area.Width - widthPx) / 2;
        var y = area.Top + area.Height / 6;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

        Show();
        Activate();
        FilterBox.Focus();
    }

    // Files dropped on a button that accepts them (today: audio onto "Audio"). A drag from Explorer deactivates the
    // panel, which then closes unless it is pinned; pressing the hotkey mid-drag brings it back under the cursor.
    private void OnActionDragOver(object sender, DragEventArgs e)
    {
        var accepts = ((FrameworkElement)sender).DataContext is PanelActionItem item
                      && DroppedPaths(e) is { } paths && item.CanAccept(paths);
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnActionDrop(object sender, DragEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PanelActionItem item && DroppedPaths(e) is { } paths && item.CanAccept(paths))
        {
            e.Handled = true;
            await item.DropAsync(paths);
        }
    }

    // Files dropped on a folder of the list are copied into it. Selecting the item under the cursor is the visual cue.
    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        var item = (ListBoxItem)sender;
        var accepts = item.DataContext is FolderEntry folder && DroppedPaths(e) is { } paths && FolderDropper.CanAccept(folder, paths);
        if (accepts)
        {
            item.IsSelected = true;
        }

        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not FolderEntry folder || DroppedPaths(e) is not { } paths
            || !FolderDropper.CanAccept(folder, paths))
        {
            return;
        }

        e.Handled = true;
        ViewModel.Shell.HidePanel();
        var (message, isWarning) = await FolderDropper.CopyAsync(folder, paths);
        ViewModel.Shell.Notify("DeskPanel", message, isWarning);
    }

    private static string[]? DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Enter when FilterBox.IsKeyboardFocused:
                if (FolderList.Items.Count > 0)
                {
                    FolderList.SelectedIndex = 0;
                    OpenSelectedFolder();
                }

                e.Handled = true;
                break;
            case Key.Enter when FolderList.IsKeyboardFocusWithin:
                OpenSelectedFolder();
                e.Handled = true;
                break;
            case Key.Down when FilterBox.IsKeyboardFocused && FolderList.Items.Count > 0:
                FolderList.SelectedIndex = 0;
                (FolderList.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OpenSelectedFolder()
    {
        if (FolderList.SelectedItem is FolderEntry entry)
        {
            ViewModel.OpenFolderCommand.Execute(entry);
        }
    }

    private static double GetMonitorScale(int x, int y)
    {
        var monitor = MonitorFromPoint(new NativePoint { X = x, Y = y }, MonitorDefaultToNearest);
        return GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
