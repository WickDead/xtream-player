using XtreamPlayer.Core.Library;

namespace XtreamPlayer.Application;

/// <summary>Catalog projection, cached ordering and filtering used by both frontends.</summary>
public sealed class BrowseWorkflow<T>(IReadOnlyList<T> source, Func<T, int, FilterableItem> project)
{
    private readonly FilterableItem[] _items = source.Select(project).ToArray();
    private CatalogOrdering? _ordering;

    public IReadOnlyList<T> Apply(FilterRequest request)
    {
        _ordering ??= new CatalogOrdering(_items);
        return CatalogFilter.Apply(_items, request, _ordering).Select(i => source[i]).ToList();
    }
}
