using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Core.Library;

/// <summary>A category id/name pair a resolver was asked to repoint.</summary>
public sealed record CategoryRepoint(string OldCategoryId, string NewCategoryId);

/// <summary>
/// Decides whether a category is hidden, tolerating providers that rename
/// categories or reassign their ids.
/// </summary>
/// <remarks>
/// Matching runs in two tiers:
/// <list type="number">
/// <item>Exact category id. Cheap and unambiguous.</item>
/// <item>Normalized name, but only for ids not present in the hidden set. This
/// is what survives a provider renumbering its categories.</item>
/// </list>
/// A tier-2 hit is recorded in <see cref="Repoints"/> so the caller can update
/// the stored row and get an exact id match next time.
/// </remarks>
public sealed class HiddenCategoryResolver
{
    private readonly HashSet<string> _hiddenIds;
    private readonly Dictionary<string, string> _hiddenIdByName;
    private readonly List<CategoryRepoint> _repoints = [];

    public HiddenCategoryResolver(IEnumerable<HiddenCategory> hidden)
    {
        ArgumentNullException.ThrowIfNull(hidden);

        _hiddenIds = new HashSet<string>(StringComparer.Ordinal);
        _hiddenIdByName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in hidden)
        {
            _hiddenIds.Add(entry.CategoryId);

            var key = Normalize(entry.CategoryName);
            if (key.Length > 0)
            {
                // First writer wins; duplicates are harmless because both rows
                // are hidden anyway.
                _hiddenIdByName.TryAdd(key, entry.CategoryId);
            }
        }
    }

    /// <summary>
    /// Stale rows matched by name whose stored id should be updated. Empty until
    /// <see cref="IsHidden"/> has been called.
    /// </summary>
    public IReadOnlyList<CategoryRepoint> Repoints => _repoints;

    public bool IsHidden(string categoryId, string? categoryName)
    {
        if (_hiddenIds.Contains(categoryId))
        {
            return true;
        }

        var key = Normalize(categoryName);
        if (key.Length > 0 && _hiddenIdByName.TryGetValue(key, out var staleId))
        {
            // The name matches a hidden row whose id no longer exists in the
            // catalog: the provider renumbered. Honour the hide and self-heal.
            if (!_repoints.Any(r => r.OldCategoryId == staleId))
            {
                _repoints.Add(new CategoryRepoint(staleId, categoryId));
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Case- and punctuation-insensitive name key. Providers are inconsistent
    /// about separators and casing between refreshes.
    /// </summary>
    internal static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        Span<char> buffer = name.Length <= 128 ? stackalloc char[name.Length] : new char[name.Length];
        var length = 0;

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }
}
