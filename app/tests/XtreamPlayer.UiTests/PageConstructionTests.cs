using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Xunit.Abstractions;
using XtreamPlayer.App.Views;

namespace XtreamPlayer.UiTests;

/// <summary>
/// Every page builds, shows and comes apart.
/// </summary>
/// <remarks>
/// <para>
/// Broad rather than deep, and deliberately so. A page whose XAML references a
/// resource that does not exist, a converter that is not there, or a binding the
/// compiler accepted but the runtime cannot resolve, fails the moment it is
/// shown — and until now the only thing that ever showed a page was a person
/// clicking on it.
/// </para>
/// <para>
/// Found by reflection rather than listed, so a page added later is covered
/// without anyone remembering to add it here. That is the same reason the view
/// locator has a test: the Downloads page was registered, routed, and rendered
/// as the words "No view for DownloadsPageViewModel".
/// </para>
/// </remarks>
public sealed class PageConstructionTests(ITestOutputHelper output)
{
    public static TheoryData<string> PageTypeNames()
    {
        var data = new TheoryData<string>();

        foreach (var type in PageTypes())
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    private static IEnumerable<Type> PageTypes() =>
        typeof(DashboardPage).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsPublic: true }
                     && t.Namespace == typeof(DashboardPage).Namespace
                     && typeof(UserControl).IsAssignableFrom(t)
                     && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    [Fact]
    public void ThereArePagesToTest()
    {
        // A reflection filter that quietly matches nothing would make every
        // theory below vacuous.
        var names = PageTypes().Select(t => t.Name).ToList();

        output.WriteLine(string.Join(", ", names));

        Assert.True(names.Count >= 10, $"Only found {names.Count} pages: {string.Join(", ", names)}");
    }

    [AvaloniaTheory]
    [MemberData(nameof(PageTypeNames))]
    public void APageCanBeShownAndRemoved(string typeName)
    {
        var type = typeof(DashboardPage).Assembly.GetType(typeName)
            ?? throw new InvalidOperationException($"No such view: {typeName}");

        var window = new Window { Width = 1280, Height = 800 };

        // No DataContext on purpose. Compiled bindings must tolerate a null
        // source — a page that only works once its view model is attached is a
        // page that throws while the shell is still wiring it up.
        window.Content = (Control)Activator.CreateInstance(type)!;
        window.Show();

        Dispatcher.UIThread.RunJobs();

        window.Content = null;

        Dispatcher.UIThread.RunJobs();

        window.Close();
    }
}
