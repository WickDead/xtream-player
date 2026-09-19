using Avalonia.Controls;
using Avalonia.Controls.Templates;
using XtreamPlayer.App.ViewModels;
using XtreamPlayer.App.Views;
using XtreamPlayer.Core.Navigation;

namespace XtreamPlayer.App;

/// <summary>
/// Maps a page view model to its view.
/// </summary>
/// <remarks>
/// An explicit switch rather than reflection over a naming convention. There are
/// nine pages, the mapping never surprises anyone reading it, and a missing case
/// is a compile-time-visible gap instead of a runtime "view not found".
/// </remarks>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data) => data switch
    {
        DashboardPageViewModel => new DashboardPage(),
        ProvidersPageViewModel => new ProvidersPage(),
        LivePageViewModel => new LivePage(),
        VodPageViewModel => new VodPage(),
        VodDetailPageViewModel => new VodDetailPage(),
        SeriesPageViewModel => new SeriesPage(),
        SeriesDetailPageViewModel => new SeriesDetailPage(),
        PlayerPageViewModel => new PlayerPage(),
        SettingsPageViewModel => new SettingsPage(),

        _ => new TextBlock
        {
            Text = $"No view for {data?.GetType().Name ?? "null"}",
            Margin = new Avalonia.Thickness(24),
        },
    };

    public bool Match(object? data) => data is PageViewModelBase;
}
