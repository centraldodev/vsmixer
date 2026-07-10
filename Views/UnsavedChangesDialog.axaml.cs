using Avalonia.Controls;
using Avalonia.Interactivity;

namespace VSMixer.Views;

public enum UnsavedChangesChoice
{
    Cancel,
    Discard,
    Save
}

public partial class UnsavedChangesDialog : Window
{
    public UnsavedChangesDialog()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            if (!args.IsProgrammatic)
            {
                args.Cancel = true;
                Close(UnsavedChangesChoice.Cancel);
            }
        };
    }

    public UnsavedChangesDialog(string message)
        : this()
    {
        MessageText.Text = message;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedChangesChoice.Cancel);
    }

    private void Discard_Click(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedChangesChoice.Discard);
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Close(UnsavedChangesChoice.Save);
    }
}
