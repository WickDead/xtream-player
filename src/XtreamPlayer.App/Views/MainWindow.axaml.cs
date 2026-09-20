using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using XtreamPlayer.App.ViewModels;

namespace XtreamPlayer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Tunnel, and handledEventsToo, so a ListBoxItem cannot swallow the
        // mouse back/forward buttons before the window sees them.
        AddHandler(PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);

        KeyDown += OnKeyDown;

        Opened += async (_, _) =>
        {
            if (DataContext is ShellViewModel shell)
            {
                await shell.InitializeAsync();
            }
        };
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsXButton1Pressed)
        {
            _ = shell.GoBackAsync();
            e.Handled = true;
        }
        else if (properties.IsXButton2Pressed)
        {
            _ = shell.GoForwardAsync();
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel shell)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left when e.KeyModifiers == KeyModifiers.Alt:
                _ = shell.GoBackAsync();
                e.Handled = true;
                break;

            case Key.Right when e.KeyModifiers == KeyModifiers.Alt:
                _ = shell.GoForwardAsync();
                e.Handled = true;
                break;

            // Backspace only navigates when the user is not typing. Getting this
            // wrong is the single most common bug in this feature: correcting a
            // typo in the search box would throw you off the page.
            case Key.Back when !IsTextInputFocused():
                _ = shell.GoBackAsync();
                e.Handled = true;
                break;

            case Key.F when e.KeyModifiers == KeyModifiers.Control:
                FocusSearch();
                e.Handled = true;
                break;
        }
    }

    private bool IsTextInputFocused()
    {
        var focused = FocusManager?.GetFocusedElement();
        return focused is TextBox or AutoCompleteBox
               || (focused as Control)?.FindAncestorOfType<TextBox>() is not null;
    }

    private void FocusSearch()
    {
        foreach (var box in this.GetVisualDescendants().OfType<TextBox>())
        {
            if (box.Watermark == "Search everything")
            {
                box.Focus();
                box.SelectAll();
                return;
            }
        }
    }
}
