using Avalonia;
using Avalonia.Headless;
using XtreamPlayer.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace XtreamPlayer.UiTests;

/// <summary>
/// Runs the real application headlessly, so tests drive real views.
/// </summary>
/// <remarks>
/// <para>
/// This exists because two crashes reached a shipped build that no amount of
/// pure-logic testing could have caught. Both were visual-tree lifecycle bugs in
/// the player's teardown — the kind that only happen when a real control is
/// really attached and then really removed.
/// </para>
/// <para>
/// The production <see cref="App"/> is used rather than a stand-in, so these
/// tests see the same styles, the same theme tokens and the same view locator
/// the app ships with. Its dependency-injection work is gated on a classic
/// desktop lifetime, which a headless run is not, so nothing tries to reach a
/// host that was never built.
/// </para>
/// </remarks>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<XtreamPlayer.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // No Skia: these tests assert structure and lifecycle, never
                // pixels, and rendering only adds a dependency that can fail for
                // reasons unrelated to what is being tested.
                UseHeadlessDrawing = true,
            });
}
