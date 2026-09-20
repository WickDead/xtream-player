namespace XtreamPlayer.Core.Library;

/// <summary>
/// Sort orders for a catalog, computed once and reused.
/// </summary>
/// <remarks>
/// <para>
/// Sorting was the expensive half of filtering, and it was being redone on every
/// keystroke. Measured on the real catalog — 68,595 movies — a search pass cost
/// 20.7 ms, over the 16 ms frame budget, which is exactly the shape of lag that
/// makes typing feel like it is fighting you.
/// </para>
/// <para>
/// The order for a given sort mode does not depend on the search text or the
/// selected category, so it never needed recomputing. Holding a permutation per
/// mode turns each keystroke into one linear pass that emits matches in an order
/// already decided.
/// </para>
/// <para>
/// Not cached for <see cref="CategorySelection.Recent"/>, whose order comes from
/// the caller's recency list rather than the catalog. That list is capped at ten
/// items, so it is not worth the complexity.
/// </para>
/// </remarks>
public sealed class CatalogOrdering(IReadOnlyList<FilterableItem> items)
{
    private readonly Dictionary<ItemSort, int[]> _orders = [];
    private string[]? _searchNames;

    public IReadOnlyList<FilterableItem> Items { get; } =
        items ?? throw new ArgumentNullException(nameof(items));

    /// <summary>
    /// Names lowered once, for ordinal substring matching.
    /// </summary>
    /// <remarks>
    /// Case-insensitive <c>Contains</c> folds case on every comparison, and a
    /// keystroke compares against every name in the catalog. Lowering once and
    /// matching ordinally moves that work out of the typing path.
    /// </remarks>
    public string[] SearchNames
    {
        get
        {
            if (_searchNames is not null)
            {
                return _searchNames;
            }

            var names = new string[Items.Count];

            for (var i = 0; i < names.Length; i++)
            {
                names[i] = Items[i].Name.ToLowerInvariant();
            }

            _searchNames = names;

            return names;
        }
    }

    /// <summary>Positions into <see cref="Items"/>, in the given sort order.</summary>
    public int[] For(ItemSort sort)
    {
        if (_orders.TryGetValue(sort, out var cached))
        {
            return cached;
        }

        var order = new int[Items.Count];

        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        var source = Items;

        Comparison<int> comparison = sort switch
        {
            // Ties break on the catalog's own position, so the order is fully
            // determined. Array.Sort and List.Sort are both unstable, and
            // without a total order the same catalog could come back shuffled
            // between one path and the other, or between two runs.
            ItemSort.Name => (a, b) =>
            {
                var byName = NaturalComparer.Instance.Compare(source[a].Name, source[b].Name);

                return byName != 0
                    ? byName
                    : source[a].SourceIndex.CompareTo(source[b].SourceIndex);
            },

            ItemSort.Newest => (a, b) =>
            {
                // Items with no date sort last rather than jumbling with 1970.
                var byDate = (source[b].Added ?? DateTimeOffset.MinValue)
                    .CompareTo(source[a].Added ?? DateTimeOffset.MinValue);

                if (byDate != 0)
                {
                    return byDate;
                }

                var byName = NaturalComparer.Instance.Compare(source[a].Name, source[b].Name);

                return byName != 0
                    ? byName
                    : source[a].SourceIndex.CompareTo(source[b].SourceIndex);
            },

            // Provider order: category first, then the provider's own ordering
            // within it.
            _ => (a, b) =>
            {
                var byCategory = source[a].CategoryOrder.CompareTo(source[b].CategoryOrder);

                return byCategory != 0
                    ? byCategory
                    : source[a].SourceIndex.CompareTo(source[b].SourceIndex);
            },
        };

        Array.Sort(order, comparison);

        _orders[sort] = order;

        return order;
    }
}
