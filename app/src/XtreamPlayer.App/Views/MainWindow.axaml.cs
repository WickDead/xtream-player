using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using XtreamPlayer.App.ViewModels;

namespace XtreamPlayer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        // InitializeComponent(), never AvaloniaXamlLoader.Load(this): only the
        // former assigns the generated x:Name fields.
        InitializeComponent();

        // Loading happens after Opened, not in the constructor, so the window is
        // on screen before any database work starts.
        Opened += async (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
            {
                await shell.LoadAsync();
            }
        };
    }
}
