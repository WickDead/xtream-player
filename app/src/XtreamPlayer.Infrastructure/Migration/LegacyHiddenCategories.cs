using System.Text.Json;
using XtreamPlayer.Core.Models;

namespace XtreamPlayer.Infrastructure.Migration;

/// <summary>
/// Hidden category names exported from the old app's localStorage.
/// </summary>
/// <remarks>
/// <para>
/// The previous app stored these under <c>xtream-hidden-categories-live</c>,
/// <c>-vod</c> and <c>-series</c> in WebView2's localStorage — a Chromium
/// LevelDB store. Parsing LevelDB properly would mean a native dependency for
/// what is, in practice, a handful of category names, and it breaks as soon as
/// WebView2 compacts the log into a snappy-compressed .ldb.
/// </para>
/// <para>
/// So the supported path is an export: run the old app, open devtools, and
/// evaluate
/// <code>
/// JSON.stringify({
///   live:   localStorage['xtream-hidden-categories-live'],
///   vod:    localStorage['xtream-hidden-categories-vod'],
///   series: localStorage['xtream-hidden-categories-series'],
/// })
/// </code>
/// then paste the result into the import wizard.
/// </para>
/// <para>
/// Because the values are themselves JSON strings, each scope arrives
/// double-encoded. Both forms are accepted, along with a plain array, so a user
/// pasting any reasonable shape gets a sensible result rather than an error.
/// </para>
/// </remarks>
public sealed class LegacyHiddenCategories
{
    private LegacyHiddenCategories(IReadOnlyDictionary<CategoryScope, IReadOnlyList<string>> byScope) =>
        ByScope = byScope;

    public IReadOnlyDictionary<CategoryScope, IReadOnlyList<string>> ByScope { get; }

    public int TotalCount => ByScope.Values.Sum(v => v.Count);

    public static LegacyHiddenCategories Empty { get; } =
        new(new Dictionary<CategoryScope, IReadOnlyList<string>>());

    public static LegacyHiddenCategories Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException(
                "Expected an object keyed by live, vod and series.");
        }

        var result = new Dictionary<CategoryScope, IReadOnlyList<string>>();

        foreach (var scope in Enum.GetValues<CategoryScope>())
        {
            if (!root.TryGetProperty(scope.ToStorageString(), out var value))
            {
                continue;
            }

            var names = ReadNames(value);
            if (names.Count > 0)
            {
                result[scope] = names;
            }
        }

        return new LegacyHiddenCategories(result);
    }

    private static IReadOnlyList<string> ReadNames(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return [];

            case JsonValueKind.Array:
                return [.. value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)];

            case JsonValueKind.String:
            {
                // The common case: localStorage values are strings, so the
                // exported object holds a JSON array encoded inside a string.
                var inner = value.GetString();
                if (string.IsNullOrWhiteSpace(inner))
                {
                    return [];
                }

                try
                {
                    using var nested = JsonDocument.Parse(inner);
                    return ReadNames(nested.RootElement);
                }
                catch (JsonException)
                {
                    // Not nested JSON — treat it as a single category name.
                    return [inner.Trim()];
                }
            }

            default:
                return [];
        }
    }
}
