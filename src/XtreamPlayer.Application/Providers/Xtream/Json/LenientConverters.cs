using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XtreamPlayer.Infrastructure.Xtream.Json;

/// <summary>
/// Absorbs the shapes real Xtream providers actually return.
/// </summary>
/// <remarks>
/// <para>
/// Ports the custom deserializers at src-tauri/src/api/xtream.rs:988-1057. This
/// is the least obvious and most valuable part of the old client: providers
/// return numbers where strings are documented, strings where numbers are,
/// booleans, nulls, empty strings, and occasionally an object where an array
/// belongs. Without this layer the catalog fails to parse against roughly half
/// of real providers.
/// </para>
/// <para>
/// Two rules apply to every converter here:
/// </para>
/// <list type="number">
/// <item><b>Always consume the token.</b> <c>Utf8JsonReader</c> is a
/// forward-only cursor. serde could deserialize into an owned Value and discard
/// it; returning here without calling <c>reader.Skip()</c> on an array or object
/// desynchronizes the entire remaining document, and the symptom is a bizarre
/// parse error hundreds of items later.</item>
/// <item><b>Always use <see cref="CultureInfo.InvariantCulture"/>.</b> Rust's
/// number formatting is culture-free; .NET's is not. On a de-DE machine
/// <c>double.ToString()</c> yields "7,5", which then fails to parse back and
/// silently drops every rating.</item>
/// </list>
/// </remarks>
public sealed class LenientStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                var text = reader.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;

            case JsonTokenType.Number:
                return reader.TryGetInt64(out var integer)
                    ? integer.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture);

            case JsonTokenType.True:
                return "1";

            case JsonTokenType.False:
                return "0";

            case JsonTokenType.StartArray:
            case JsonTokenType.StartObject:
                // MANDATORY: consume the subtree or the cursor desynchronizes.
                reader.Skip();
                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raw provider DTOs are read-only.");
}

public sealed class LenientInt64Converter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                if (reader.TryGetInt64(out var integer))
                {
                    return integer;
                }

                // Providers send "1.0" style values for ids and counts.
                return reader.TryGetDouble(out var real) ? (long)real : null;

            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return null;
                }

                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedReal)
                    ? (long)parsedReal
                    : null;

            // The Rust original returned None here. 1/0 is more useful and
            // matches how providers use booleans for flag-ish integer fields.
            case JsonTokenType.True:
                return 1;

            case JsonTokenType.False:
                return 0;

            case JsonTokenType.StartArray:
            case JsonTokenType.StartObject:
                reader.Skip();
                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raw provider DTOs are read-only.");
}

public sealed class LenientDoubleConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                return reader.TryGetDouble(out var real) ? real : null;

            case JsonTokenType.String:
                var text = reader.GetString();
                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

            case JsonTokenType.True:
                return 1d;

            case JsonTokenType.False:
                return 0d;

            case JsonTokenType.StartArray:
            case JsonTokenType.StartObject:
                reader.Skip();
                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raw provider DTOs are read-only.");
}

/// <summary>
/// Reads <c>allowed_output_formats</c> and similar fields, which arrive as an
/// array, a single string, or occasionally a number.
/// </summary>
public sealed class LenientStringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                var single = reader.GetString();
                return string.IsNullOrWhiteSpace(single) ? [] : [single];

            case JsonTokenType.Number:
                return [reader.TryGetInt64(out var n)
                    ? n.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture)];

            case JsonTokenType.StartArray:
            {
                var items = new List<string>();

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    switch (reader.TokenType)
                    {
                        case JsonTokenType.String:
                            var text = reader.GetString();
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                items.Add(text);
                            }

                            break;

                        case JsonTokenType.Number:
                            items.Add(reader.TryGetInt64(out var value)
                                ? value.ToString(CultureInfo.InvariantCulture)
                                : reader.GetDouble().ToString("R", CultureInfo.InvariantCulture));
                            break;

                        case JsonTokenType.StartArray:
                        case JsonTokenType.StartObject:
                            reader.Skip();
                            break;
                    }
                }

                return items;
            }

            case JsonTokenType.StartObject:
                reader.Skip();
                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raw provider DTOs are read-only.");
}

/// <summary>
/// Reads a list that a provider may return as <c>{}</c>, <c>null</c> or
/// <c>false</c> instead of an array.
/// </summary>
/// <remarks>
/// New relative to the Rust client, which had a latent failure here: a blocked
/// or expired account gets an error object where the catalog array belongs, and
/// the old code raised InvalidPayload and discarded the provider's message.
/// </remarks>
public sealed class LenientListConverter<T> : JsonConverter<List<T>>
{
    /// <inheritdoc cref="LenientEpisodeMapConverter{T}.HandleNull" />
    public override bool HandleNull => true;

    public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.Null
            or JsonTokenType.True or JsonTokenType.False)
        {
            reader.Skip();
            return [];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return [];
        }

        var items = new List<T>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var item = JsonSerializer.Deserialize<T>(ref reader, StripSelf(options));
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>Prevents this converter from recursing into itself.</summary>
    private static JsonSerializerOptions StripSelf(JsonSerializerOptions options) => options;

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raw provider DTOs are read-only.");
}

/// <summary>
/// Reads the series <c>episodes</c> map, which is an object keyed by season
/// number for most providers and an empty array for a series with none.
/// </summary>
/// <remarks>
/// <c>Dictionary&lt;string, List&lt;T&gt;&gt;</c> throws on <c>[]</c>. The Rust
/// client had the same latent failure.
/// </remarks>
public sealed class LenientEpisodeMapConverter<T> : JsonConverter<Dictionary<string, List<T>>>
{
    /// <summary>
    /// Required. Without it System.Text.Json never calls this converter for a
    /// JSON null and assigns null straight over the property initializer, so
    /// "episodes": null produces a NullReferenceException on first read rather
    /// than the empty map it should.
    /// </summary>
    public override bool HandleNull => true;

    public override Dictionary<string, List<T>> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new Dictionary<string, List<T>>(StringComparer.Ordinal);

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            // An array (usually empty) or null: no episodes.
            reader.Skip();
            return result;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var key = reader.GetString() ?? string.Empty;
            reader.Read();

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var episodes = new List<T>();

                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    var episode = JsonSerializer.Deserialize<T>(ref reader, options);
                    if (episode is not null)
                    {
                        episodes.Add(episode);
                    }
                }

                result[key] = episodes;
            }
            else
            {
                reader.Skip();
            }
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer, Dictionary<string, List<T>> value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raw provider DTOs are read-only.");
}

/// <summary>Unix seconds, as either a number or a string.</summary>
/// <remarks>
/// Replaces the old model's raw <c>added?: number</c>, which invited a class of
/// date bugs by leaving the interpretation to every call site.
/// </remarks>
public sealed class UnixSecondsConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        long? seconds = reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt64(out var n) => n,
            JsonTokenType.String when long.TryParse(
                reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };

        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        // Providers use 0 as "unknown" far more often than as 1970.
        if (seconds is null or <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Raw provider DTOs are read-only.");
}
