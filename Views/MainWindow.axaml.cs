using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using System;
using System.Linq;
using System.Threading.Tasks;
using VSMixer.ViewModels;

namespace VSMixer.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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
            viewModel.ImportTracks(paths);
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

        var path = await PickSaveProjectPath();
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

        var path = await PickSaveProjectPath();
        if (!string.IsNullOrWhiteSpace(path))
        {
            viewModel.SaveProject(path);
        }
    }

    private async Task<string?> PickSaveProjectPath()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Salvar projeto VSMixer",
            SuggestedFileName = "Projeto.vsmixer",
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
