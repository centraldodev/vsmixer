using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using VSMixer.ViewModels;

namespace VSMixer.Views;

public partial class MainWindow : Window
{
    private bool _forceClose;
    private bool _isCloseDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        Opened += MainWindow_Opened;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        KeyDown += MainWindow_KeyDown;
    }

    private async void MainWindow_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var commandModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (commandModifier && e.Key == Key.S)
        {
            if (viewModel.HasSavedProject)
            {
                viewModel.SaveCurrentProject();
            }
            else
            {
                var path = await PickSaveProjectPath(viewModel.ProjectName);
                if (!string.IsNullOrWhiteSpace(path))
                {
                    viewModel.SaveProject(path);
                }
            }

            e.Handled = true;
            return;
        }

        if (e.Source is TextBox or ComboBox)
        {
            return;
        }

        if (e.Key == Key.Space)
        {
            viewModel.TogglePlaybackCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            viewModel.RewindCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            viewModel.EmergencyStopCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void MainWindow_Opened(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DirtyTabCloseRequested += OnDirtyTabCloseRequested;
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.DirtyTabCloseRequested -= OnDirtyTabCloseRequested;
        }
    }

    private async void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || DataContext is not MainWindowViewModel { HasUnsavedChanges: true } viewModel)
        {
            return;
        }

        e.Cancel = true;
        if (_isCloseDialogOpen)
        {
            return;
        }

        _isCloseDialogOpen = true;
        try
        {
            var dialog = new UnsavedChangesDialog("Existem projetos com alterações não salvas. Deseja salvá-los antes de sair?");
            var choice = await dialog.ShowDialog<UnsavedChangesChoice>(this);
            if (choice == UnsavedChangesChoice.Cancel)
            {
                return;
            }

            if (choice == UnsavedChangesChoice.Save && !await SaveAllDirtyProjects(viewModel))
            {
                return;
            }

            if (choice == UnsavedChangesChoice.Discard)
            {
                viewModel.DiscardRecoveryOnExit();
            }

            _forceClose = true;
            Close();
        }
        finally
        {
            _isCloseDialogOpen = false;
        }
    }

    private async void OnDirtyTabCloseRequested(ProjectTabViewModel tab)
    {
        if (_isCloseDialogOpen || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _isCloseDialogOpen = true;
        try
        {
            var dialog = new UnsavedChangesDialog($"O projeto ‘{tab.Name}’ possui alterações não salvas.");
            var choice = await dialog.ShowDialog<UnsavedChangesChoice>(this);
            if (choice == UnsavedChangesChoice.Cancel)
            {
                return;
            }

            if (choice == UnsavedChangesChoice.Save)
            {
                viewModel.ActiveProjectTab = tab;
                if (!await SaveTab(viewModel, tab))
                {
                    return;
                }
            }

            viewModel.DiscardAndCloseProjectTab(tab);
        }
        finally
        {
            _isCloseDialogOpen = false;
        }
    }

    private async Task<bool> SaveAllDirtyProjects(MainWindowViewModel viewModel)
    {
        foreach (var tab in viewModel.ProjectTabs.Where(tab => tab.IsDirty).ToArray())
        {
            viewModel.ActiveProjectTab = tab;
            if (!await SaveTab(viewModel, tab))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> SaveTab(MainWindowViewModel viewModel, ProjectTabViewModel tab)
    {
        var path = tab.ProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = await PickSaveProjectPath(tab.Name);
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
        }

        viewModel.SaveProject(path);
        return !tab.IsDirty;
    }

    private async void ImportTracksButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importar multitrack",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio")
                {
                    Patterns = ["*.wav", "*.mp3", "*.aiff", "*.aif", "*.ogg", "*.flac"]
                },
                FilePickerFileTypes.All
            ]
        });

        var paths = files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray();

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.ImportTracksAsync(paths);
        }
    }

    private void WaveformView_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var width = Math.Max(1, control.Bounds.Width);
        var pointer = e.GetPosition(control);
        viewModel.SeekToProgress(pointer.X / width);
        e.Handled = true;
    }

    private async void OpenProjectMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Abrir projeto VSMixer",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Projeto VSMixer")
                {
                    Patterns = ["*.vsmixer"]
                },
                FilePickerFileTypes.All
            ]
        });

        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path) && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.OpenProject(path);
        }
    }

    private async void SaveProjectMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (viewModel.HasSavedProject)
        {
            viewModel.SaveCurrentProject();
            return;
        }

        var path = await PickSaveProjectPath(viewModel.ProjectName);
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.SaveProject(path);
        }
    }

    private async void SaveProjectAsMenuItem_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var path = await PickSaveProjectPath(viewModel.ProjectName);
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.SaveProject(path);
        }
    }

    private async Task<string?> PickSaveProjectPath(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar projeto VSMixer",
            SuggestedFileName = $"{suggestedName}.vsmixer",
            DefaultExtension = "vsmixer",
            FileTypeChoices =
            [
                new FilePickerFileType("Projeto VSMixer")
                {
                    Patterns = ["*.vsmixer"]
                }
            ]
        });

        return file?.TryGetLocalPath();
    }
}
