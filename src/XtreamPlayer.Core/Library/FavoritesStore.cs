using System.Collections.ObjectModel;
using XtreamPlayer.Core.Abstractions;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Library;

public sealed class FavoritesChangedEventArgs(ContentKind kind, string contentId, bool isFavorite)
    : EventArgs
{
    public ContentKind Kind { get; } = kind;
    public string ContentId { get; } = contentId;
    public bool IsFavorite { get; } = isFavorite;
}

public interface IFavoritesStore
{
    IReadOnlyList<FavoriteItem> All { get; }

    bool IsFavorite(ContentKind kind, string contentId);

    IReadOnlySet<string> IdsFor(ContentKind kind);

    Task LoadAsync(ProfileScope scope, CancellationToken ct = default);

    /// <summary>Returns the new state.</summary>
    Task<bool> ToggleAsync(ContentKind kind, string contentId, string? title, CancellationToken ct = default);

    event EventHandler<FavoritesChangedEventArgs>? Changed;
}

/// <summary>
/// Favorites held in memory, written through to the database.
/// </summary>
/// <remarks>
/// <para>
/// The previous app called <c>refreshBootstrap()</c> after every single star
/// click — a full round trip that re-read every profile, favorite and resume row
/// just to toggle one flag. Here a toggle is one row write plus a set update,
/// and the UI updates from the in-memory set immediately.
/// </para>
/// <para>
/// Sets are kept per kind because the Favorites pseudo-category filters one
/// catalog at a time, and because live and VOD content ids share a namespace —
/// a single combined set would conflate channel 1234 with movie 1234.
/// </para>
/// </remarks>
public sealed class FavoritesStore(IFavoritesRepository repository) : IFavoritesStore
{
    private readonly Dictionary<ContentKind, HashSet<string>> _byKind = new()
    {
        [ContentKind.Live] = new HashSet<string>(StringComparer.Ordinal),
        [ContentKind.Vod] = new HashSet<string>(StringComparer.Ordinal),
        [ContentKind.Series] = new HashSet<string>(StringComparer.Ordinal),
    };

    private readonly ObservableCollection<FavoriteItem> _all = [];
    private ProfileScope? _scope;

    public IReadOnlyList<FavoriteItem> All => _all;

    public event EventHandler<FavoritesChangedEventArgs>? Changed;

    public bool IsFavorite(ContentKind kind, string contentId) =>
        _byKind[kind].Contains(contentId);

    public IReadOnlySet<string> IdsFor(ContentKind kind) => _byKind[kind];

    public async Task LoadAsync(ProfileScope scope, CancellationToken ct = default)
    {
        _scope = scope;

        foreach (var set in _byKind.Values)
        {
            set.Clear();
        }

        _all.Clear();

        foreach (var favorite in await repository.ListAsync(scope, ct).ConfigureAwait(false))
        {
            _byKind[favorite.Kind].Add(favorite.ContentId);
            _all.Add(favorite);
        }
    }

    public async Task<bool> ToggleAsync(
        ContentKind kind, string contentId, string? title, CancellationToken ct = default)
    {
        if (_scope is not { } scope)
        {
            return false;
        }

        var set = _byKind[kind];

        if (set.Contains(contentId))
        {
            // Optimistic: the set updates first so the star flips instantly.
            set.Remove(contentId);

            for (var i = _all.Count - 1; i >= 0; i--)
            {
                if (_all[i].Kind == kind && _all[i].ContentId == contentId)
                {
                    _all.RemoveAt(i);
                }
            }

            await repository.RemoveAsync(scope, contentId, kind, ct).ConfigureAwait(false);
            Changed?.Invoke(this, new FavoritesChangedEventArgs(kind, contentId, false));
            return false;
        }

        set.Add(contentId);
        var item = await repository.AddAsync(scope, contentId, kind, title, ct).ConfigureAwait(false);
        _all.Add(item);

        Changed?.Invoke(this, new FavoritesChangedEventArgs(kind, contentId, true));
        return true;
    }
}
