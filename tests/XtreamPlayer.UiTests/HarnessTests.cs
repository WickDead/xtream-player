using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace XtreamPlayer.UiTests;

/// <summary>
/// That the harness itself works, before anything relies on it.
/// </summary>
public sealed class HarnessTests
{
    [AvaloniaFact]
    public void TheApplicationStartsAndCarriesItsTheme()
    {
        var app = Avalonia.Application.Current;

        Assert.NotNull(app);

        // A token from the app's own Themes/Tokens.axaml. If this resolves, the
        // real styles loaded and views will look up resources the way they do in
        // production rather than falling back to Fluent defaults.
        Assert.True(
            app!.TryFindResource("Accent", Avalonia.Styling.ThemeVariant.Dark, out var accent),
            "The app's theme tokens did not load.");

        Assert.NotNull(accent);
    }

    [AvaloniaFact]
    public void AWindowCanBeOpenedAndClosed()
    {
        var window = new Window { Content = new TextBlock { Text = "hello" } };

        window.Show();

        Assert.True(window.IsVisible);

        window.Close();
    }
}
