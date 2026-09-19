using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XtreamPlayer.App.Behaviors;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App.ViewModels;

public sealed partial class SeriesItemViewModel(SeriesItem item, bool isFavorite)
    : ObservableObject, IScrollKeyed
{
    public SeriesItem Item { get; } = item;

    public string Id => Item.Id;
    public string Name => Item.Name;
    public string? PosterUrl => Item.PosterUrl;
    public string? Rating => Item.Rating;
    public string ScrollKey => Item.Id;

    public bool HasRating => !string.IsNullOrEmpty(Item.Rating);

    public string? YearLabel => Item.LastModified?.Year.ToString();

    [ObservableProperty]
    private bool _isFavorite = isFavorite;
}

public sealed partial class SeriesPageViewModel(
    ICatalogStore catalog,
    IProfileRepository profiles,
    IFavoritesStore favorites,
    IHiddenCategoryRepository hidden,
    ISettingsRepository settings,
    INavigationService navigation)
    : BrowsePageViewModel<SeriesItemViewModel>(favorites, hidden, settings, catalog)
{
    public override PageKey Key => PageKey.Series;

    public override string Title => "Series";

    protected override ContentKind Kind => ContentKind.Series;

    protected override CategoryScope Scope => CategoryScope.Series;

    protected override string ListScrollKey => "posters";

    protected override FilterableItem Project(SeriesItemViewModel item, int index) =>
        new(item.Id, item.Name, item.Item.CategoryId, item.Item.CategoryOrder,
            item.Item.LastModified, index);

    protected override string ItemIdOf(SeriesItemViewModel item) => item.Id;

    protected override string TitleOf(SeriesItemViewModel item) => item.Name;

    public override async Task OnNavigatedToAsync(
        object? parameter, bool isNewInstance, CancellationToken ct = default)
    {
        if (!isNewInstance)
        {
            return;
        }

        // Newest first is the right default for a large library; provider order
        // is close to meaningless across tens of thousands of items.
        Sort = ItemSort.Newest;

        await ReloadFromCatalogAsync().ConfigureAwait(false);
    }

    protected override async Task ReloadFromCatalogAsync()
    {
        IsBusy = true;
        try
        {
            var activeId = await settings.GetAsync(SettingKeys.ActiveProfileId).ConfigureAwait(true);
            var all = await profiles.ListAsync().ConfigureAwait(true);
            var profile = all.FirstOrDefault(p => p.Id == activeId) ?? all.FirstOrDefault();

            if (profile is null)
            {
                return;
            }

            var source = catalog.Current.Series;
            var wrapped = new List<SeriesItemViewModel>(source.Items.Count);

            foreach (var item in source.Items)
            {
                wrapped.Add(new SeriesItemViewModel(item, IsFavorite(item.Id)));
            }

            await SetSourceAsync(wrapped, source.Categories, new ProfileScope(profile.Id))
                .ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenAsync(SeriesItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        await navigation.NavigateAsync(new NavRoute(PageKey.SeriesDetail, item.Id)).ConfigureAwait(false);
    }
}
