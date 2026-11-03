using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using DeskPanel.Core.Configuration;
using Microsoft.Win32;

namespace VerificaFicheros
{
    public partial class MainWindow : Window
    {
        private FolderController m_folderController = new FolderController();

        public MainWindow()
        {
            InitializeComponent();
            var (configDirectory, paths) = ConfigService.ReadMachineBootstrap();
            m_folderController.Load(configDirectory, paths, AppContext.BaseDirectory);
            FoldersListBox.ItemsSource = m_folderController.MonitoredFolders;
        }

        // Helper to report progress to the Log TextBlock
        private void ReportProgress(string message)
        {
            Log.Text = message;
        }

        // Helper to lock UI during async operations
        private void ToggleUi(bool enabled)
        {
            FoldersListBox.IsEnabled = enabled;
            DetailListBox.IsEnabled = enabled;
            AddFolderButton.IsEnabled = enabled;
            RemoveFolderButton.IsEnabled = enabled;
            VerifyAllFolders.IsEnabled = enabled;
            CheckButton.IsEnabled = enabled;
            UpdatePendingButton.IsEnabled = enabled;
        }

        private async void AddFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog();

            if (dialog.ShowDialog().GetValueOrDefault())
            {
                try
                {
                    ToggleUi(false);
                    var progress = new Progress<string>(ReportProgress);
                    var path = dialog.FolderName;

                    // Run heavy I/O on background thread
                    await Task.Run(() => m_folderController.Add(path, progress));

                    // Update UI binding
                    FoldersListBox.ItemsSource = null;
                    FoldersListBox.ItemsSource = m_folderController.MonitoredFolders;
                    Log.Text = "Folder added successfully.";
                }
                catch (Exception ex)
                {
                    Log.Text = $"Error: {ex.Message}";
                }
                finally
                {
                    ToggleUi(true);
                }
            }
        }

        private void RemoveFolderButton_Click(object sender, RoutedEventArgs e)
        {
            // Delete is usually fast enough to stay synchronous, 
            // but can be made async if file deletion is heavy.
            if (FoldersListBox.SelectedItem == null) return;

            var selected = FoldersListBox.SelectedItem.ToString();
            if (!string.IsNullOrEmpty(selected))
            {
                m_folderController.Delete(selected);
                FoldersListBox.ItemsSource = null;
                FoldersListBox.ItemsSource = m_folderController.MonitoredFolders;
                DetailListBox.ItemsSource = null;
                Log.Text = "Folder removed.";
            }
        }

        private async void CheckButton_Click(object sender, RoutedEventArgs e)
        {
            if (FoldersListBox.SelectedItem == null) return;

            var selected = FoldersListBox.SelectedItem.ToString();
            if (!string.IsNullOrEmpty(selected))
            {
                try
                {
                    ToggleUi(false);
                    var progress = new Progress<string>(ReportProgress);

                    await Task.Run(() => m_folderController.Verify(progress));

                    DetailListBox.ItemsSource = null;
                    DetailListBox.ItemsSource = m_folderController.FilesNotMatching;

                    if (m_folderController.FilesNotMatching.Count == 0)
                        Log.Text = "Folder verified: No changes found.";
                }
                catch (Exception ex)
                {
                    Log.Text = $"Error verifying: {ex.Message}";
                }
                finally
                {
                    ToggleUi(true);
                }
            }
        }

        private async void UpdatePendingButton_Click(object sender, RoutedEventArgs e)
        {
            if (DetailListBox.SelectedItems.Count > 0)
            {
                try
                {
                    ToggleUi(false);
                    var progress = new Progress<string>(ReportProgress);

                    // Capture selected items before going to background thread
                    // to avoid cross-thread access to UI components
                    var items = new System.Collections.ArrayList(DetailListBox.SelectedItems);

                    await Task.Run(() => m_folderController.Update(items, progress));

                    DetailListBox.ItemsSource = null;
                    DetailListBox.ItemsSource = m_folderController.FilesNotMatching;
                    Log.Text = "Update complete.";
                }
                catch (Exception ex)
                {
                    Log.Text = $"Error updating: {ex.Message}";
                }
                finally
                {
                    ToggleUi(true);
                }
            }
        }

        private void FoldersListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // This is just a state switch, safe to keep synchronous/fast
            if (e.AddedItems.Count > 0)
            {
                m_folderController.Select(e.AddedItems[0].ToString());
                DetailListBox.ItemsSource = null;
                DetailListBox.ItemsSource = m_folderController.FilesNotMatching;
                Log.Text = $"Selected: {e.AddedItems[0]}";
            }
        }

        private async void VerifyAllFolders_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ToggleUi(false);
                IProgress<string> progress = new Progress<string>(ReportProgress);
                var selected = FoldersListBox.SelectedItem;

                await Task.Run(() =>
                {
                    int index = 1;
                    int total = m_folderController.MonitoredFolders.Count;

                    foreach (var line in m_folderController.MonitoredFolders)
                    {
                        progress.Report($"Processing folder {index}/{total}: {line}");
                        m_folderController.Select(line);
                        m_folderController.Verify(progress);
                        index++;
                    }
                });

                // Restore selection
                if (selected != null)
                {
                    m_folderController.Select(selected.ToString());
                    DetailListBox.ItemsSource = null;
                    DetailListBox.ItemsSource = m_folderController.FilesNotMatching;
                }

                Log.Text = "Verify All completed.";
            }
            catch (Exception ex)
            {
                Log.Text = $"Error in Verify All: {ex.Message}";
            }
            finally
            {
                ToggleUi(true);
            }
        }
    }
}