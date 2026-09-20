using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using XtreamPlayer.App.Behaviors;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Core.Models;
using XtreamPlayer.Core.Navigation;
using XtreamPlayer.Infrastructure.Xtream;

namespace XtreamPlayer.App.ViewModels;

public sealed partial class VodItemViewModel(VodItem item, bool isFavorite)
    : ObservableObject, IScrollKeyed
{
    public VodItem Item { get; } = item;

    public string Id => Item.Id;
    public string Name => Item.Name;
    public string? PosterUrl => Item.PosterUrl;
    public string? Rating => Item.Rating;
    public string ScrollKey => Item.Id;

    public bool HasRating => !string.IsNullOrEmpty(Item.Rating);

    /// <summary>
    /// When the provider added this, not when the film was released — the
    /// release year is already inside the title for VOD.
    /// </summary>
    public string? AddedYearLabel => Item.Added?.Year.ToString(
        System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isFavorite = isFavorite;
}

public sealed partial class VodPageViewModel(
    ICatalogStore catalog,
    IProfileRepository profiles,
    IFavoritesStore favorites,
    IHiddenCategoryRepository hidden,
    ISettingsRepository settings,
    INavigationService navigation)
    : BrowsePageViewModel<VodItemViewModel>(favorites, hidden, settings, catalog)
{
    public override PageKey Key => PageKey.Vod;

    public override string Title => "Movies";

    protected override ContentKind Kind => ContentKind.Vod;

    protected override CategoryScope Scope => CategoryScope.Vod;

    protected override string ListScrollKey => "posters";

    protected override FilterableItem Project(VodItemViewModel item, int index) =>
        new(item.Id, item.Name, item.Item.CategoryId, item.Item.CategoryOrder, item.Item.Added, index);

    protected override string ItemIdOf(VodItemViewModel item) => item.Id;

    protected override string TitleOf(VodItemViewModel item) => item.Name;

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

            var source = catalog.Current.Vod;
            var wrapped = new List<VodItemViewModel>(source.Items.Count);

            foreach (var item in source.Items)
            {
                wrapped.Add(new VodItemViewModel(item, IsFavorite(item.Id)));
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
    private async Task OpenAsync(VodItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null)
        {
            return;
        }

        // A push, not an inline panel. This is what makes the back stack
        // visible and useful: Back returns to the grid at the same scroll
        // offset, with the same category and filter text.
        await navigation.NavigateAsync(new NavRoute(PageKey.VodDetail, item.Id)).ConfigureAwait(false);
    }
}
