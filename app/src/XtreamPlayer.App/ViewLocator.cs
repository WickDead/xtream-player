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
/// eleven pages, the mapping never surprises anyone reading it, and a missing
/// case is visible here rather than as a runtime "view not found". A test walks
/// the view models to make sure none is forgotten.
/// </remarks>
public sealed class ViewLocator : IDataTemplate
{
    public Control Build(object? data) => data switch
    {
        DashboardPageViewModel => new DashboardPage(),
        ProvidersPageViewModel => new ProvidersPage(),
        LivePageViewModel => new LivePage(),
        GuidePageViewModel => new GuidePage(),
        VodPageViewModel => new VodPage(),
        VodDetailPageViewModel => new VodDetailPage(),
        SeriesPageViewModel => new SeriesPage(),
        SeriesDetailPageViewModel => new SeriesDetailPage(),
        PlayerPageViewModel => new PlayerPage(),
        SettingsPageViewModel => new SettingsPage(),
        DownloadsPageViewModel => new DownloadsPage(),

        _ => new TextBlock
        {
            Text = $"No view for {data?.GetType().Name ?? "null"}",
            Margin = new Avalonia.Thickness(24),
        },
    };

    public bool Match(object? data) => data is PageViewModelBase;
}
