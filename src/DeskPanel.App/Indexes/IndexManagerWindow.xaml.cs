using System.Windows;

namespace DeskPanel.App.Indexes;

internal partial class IndexManagerWindow : Window
{
    public IndexManagerWindow(IndexManagerViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
