using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace XtreamPlayer.Tests;

/// <summary>
/// Static checks over the XAML, for mistakes that are otherwise only found by
/// clicking the right button.
/// </summary>
/// <remarks>
/// These exist because of a real crash: a <c>RelayCommand&lt;double&gt;</c> bound to
/// <c>CommandParameter="-10"</c> receives the string "-10", and
/// CommunityToolkit throws ArgumentException when the button attaches to the
/// logical tree. That happens inside a layout pass, so it is not catchable and
/// takes the whole application down — and only when the page containing that
/// button is first shown. The player crashed on open for exactly this reason.
/// </remarks>
public sealed class ViewConventionTests(ITestOutputHelper output)
{
    private static string AppRoot
    {
        get
        {
            // Walk up from the test binaries to the repository's app folder.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && directory.Name != "app")
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "src", "XtreamPlayer.App");
        }
    }

    private static string ViewsDirectory => Path.Combine(AppRoot, "Views");

    private static IEnumerable<string> ViewFiles() =>
        Directory.EnumerateFiles(ViewsDirectory, "*.axaml", SearchOption.AllDirectories);

    /// <summary>
    /// A literal CommandParameter is a runtime type mismatch waiting to happen:
    /// XAML supplies a string, and any command expecting a bool, a number or an
    /// enum throws when its button is attached.
    /// </summary>
    /// <remarks>
    /// The fix is always a dedicated parameterless command — SeekBackCommand
    /// rather than SeekRelativeCommand with "-10", ResumeCommand rather than
    /// PlayCommand with "False". Binding expressions are fine, because those
    /// carry a real typed object.
    /// </remarks>
    [Fact]
    public void NoViewUsesALiteralCommandParameter()
    {
        var literal = new Regex(
            "CommandParameter=\"(?<value>[^\"{}]+)\"",
            RegexOptions.IgnoreCase);

        var offenders = new List<string>();

        foreach (var file in ViewFiles())
        {
            var text = File.ReadAllText(file);

            foreach (Match match in literal.Matches(text))
            {
                var line = text[..match.Index].Count(c => c == '\n') + 1;

                offenders.Add(
                    $"{Path.GetFileName(file)}:{line} CommandParameter=\"{match.Groups["value"].Value}\"");
            }
        }

        foreach (var offender in offenders)
        {
            output.WriteLine(offender);
        }

        Assert.True(offenders.Count == 0,
            "Literal CommandParameter values are passed as strings and crash the app "
          + "during layout when the command expects another type. Use a dedicated "
          + "parameterless command instead:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Every view must have a code-behind, or InitializeComponent is missing and
    /// the x:Name fields are never assigned.
    /// </summary>
    [Fact]
    public void EveryViewHasCodeBehind()
    {
        var missing = ViewFiles()
            .Where(file => !File.Exists(file + ".cs"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(missing.Count == 0,
            "Views without code-behind: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Views must call InitializeComponent, never AvaloniaXamlLoader.Load.
    /// </summary>
    /// <remarks>
    /// Both parse the XAML, but only InitializeComponent assigns the generated
    /// x:Name fields. Using Load leaves every named control null, and the first
    /// use is a NullReferenceException — which is how the Phase 0 spike failed
    /// to start at all.
    /// </remarks>
    [Fact]
    public void ViewsUseInitializeComponent()
    {
        var offenders = new List<string>();

        foreach (var file in ViewFiles())
        {
            var codeBehind = file + ".cs";

            if (!File.Exists(codeBehind))
            {
                continue;
            }

            var text = File.ReadAllText(codeBehind);

            if (text.Contains("AvaloniaXamlLoader.Load(this)", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(codeBehind));
            }
        }

        Assert.True(offenders.Count == 0,
            "These call AvaloniaXamlLoader.Load instead of InitializeComponent, which "
          + "leaves every x:Name field null: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The player must not bind VideoView.MediaPlayer in XAML.
    /// </summary>
    /// <remarks>
    /// LibVLCSharp.Avalonia 3.10.1 only wires MediaPlayer.Hwnd once its native
    /// handle exists, and the method that creates the handle never re-runs the
    /// wiring. Assigning the player before the control is attached fails
    /// silently: libvlc opens its own separate window instead of drawing into
    /// ours. It has to be assigned from code-behind after attach.
    /// </remarks>
    [Fact]
    public void VideoViewMediaPlayerIsNotBoundInXaml()
    {
        foreach (var file in ViewFiles())
        {
            var text = File.ReadAllText(file);

            Assert.False(
                text.Contains("VideoView", StringComparison.Ordinal)
                && text.Contains("MediaPlayer=", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} binds VideoView.MediaPlayer in XAML. Assign it "
              + "from code-behind after the control is attached, or libvlc will open its "
              + "own window.");
        }
    }

    /// <summary>
    /// Every page view model must have a case in the view locator.
    /// </summary>
    /// <remarks>
    /// The locator is an explicit switch, which is the right call for a dozen
    /// pages — but a switch has no compiler check for completeness over a set of
    /// classes, so a new page silently falls through to the "No view for
    /// X" placeholder. That is exactly what happened when the Downloads page was
    /// added: it was registered, routed, and rendered as a line of text.
    /// </remarks>
    [Fact]
    public void EveryPageViewModelIsMappedInTheViewLocator()
    {
        var locator = File.ReadAllText(Path.Combine(AppRoot, "ViewLocator.cs"));

        // The whole declaration, so abstract and generic bases can be told apart
        // from real pages. CatalogBrowseViewModelBase is a generic base class and
        // has no view of its own.
        var declaration = new System.Text.RegularExpressions.Regex(
            @"(?<modifiers>[^\r\n]*?)\bclass\s+(?<name>\w+PageViewModel)(?<generic><)?");

        var viewModels = Directory
            .EnumerateFiles(Path.Combine(AppRoot, "ViewModels"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(file => declaration.Matches(File.ReadAllText(file)).Cast<System.Text.RegularExpressions.Match>())
            .Where(m => !m.Groups["generic"].Success
                     && !m.Groups["modifiers"].Value.Contains("abstract", StringComparison.Ordinal))
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(viewModels);

        var missing = viewModels
            .Where(name => !locator.Contains(name, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "These page view models have no case in ViewLocator, so they render as a "
          + "placeholder instead of their view: " + string.Join(", ", missing));
    }
}
