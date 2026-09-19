using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Library;

/// <summary>
/// What the category rail can be showing.
/// </summary>
/// <remarks>
/// Replaces the magic strings the previous app compared by value in three
/// separate files: <c>'★ Favorites'</c>, <c>'📺 All'</c> and <c>'🎬 All'</c>.
/// Those were display text and filter key at once, so renaming a label changed
/// behaviour and the VOD and live "All" entries were different strings for the
/// same idea.
/// </remarks>
public abstract record CategorySelection
{
    public sealed record All : CategorySelection
    {
        public static All Instance { get; } = new();
    }

    public sealed record Favorites : CategorySelection
    {
        public static Favorites Instance { get; } = new();
    }

    /// <summary>Live only: recently watched channels.</summary>
    public sealed record Recent : CategorySelection
    {
        public static Recent Instance { get; } = new();
    }

    public sealed record Real(string CategoryId, string Name, int Order) : CategorySelection;

    public string DisplayName => this switch
    {
        All => "All",
        Favorites => "Favorites",
        Recent => "Recent",
        Real real => real.Name,
        _ => "Unknown",
    };

    /// <summary>Stable identity for persisting the user's last selection.</summary>
    public string PersistKey => this switch
    {
        All => "@all",
        Favorites => "@favorites",
        Recent => "@recent",
        Real real => real.CategoryId,
        _ => "@all",
    };
}

/// <summary>A category rail row, with its count and hidden state.</summary>
public sealed record CategoryRow
{
    public required CategorySelection Selection { get; init; }
    public required string Name { get; init; }
    public int Count { get; init; }
    public bool IsHidden { get; init; }

    /// <summary>Pseudo-categories cannot be hidden; there is nothing to hide.</summary>
    public bool CanHide => Selection is CategorySelection.Real;
}

public enum CategorySort
{
    /// <summary>The provider's own ordering, which is often the most meaningful.</summary>
    ProviderOrder,

    Name,

    /// <summary>Largest first.</summary>
    Size,
}

public enum ItemSort
{
    /// <summary>Provider order for live; newest first for VOD and series.</summary>
    Default,

    Name,
    Newest,
}
