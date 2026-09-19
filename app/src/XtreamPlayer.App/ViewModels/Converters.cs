using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using XtreamPlayer.Core.Library;
using XtreamPlayer.Playback;

namespace XtreamPlayer.App.ViewModels;

/// <summary>
/// Small presentation converters.
/// </summary>
/// <remarks>
/// These keep glyphs and colour decisions out of the view models. A view model
/// that knows what a filled star looks like is a view model that cannot be
/// tested without a UI.
/// </remarks>
public sealed class StarGlyphConverter : IValueConverter
{
    public static StarGlyphConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "★" : "☆";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class FavoriteBrushConverter : IValueConverter
{
    public static FavoriteBrushConverter Instance { get; } = new();

    private static readonly IBrush Favorited = new SolidColorBrush(Color.Parse("#FFC44D"));
    private static readonly IBrush Plain = new SolidColorBrush(Color.Parse("#5A5A70"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Favorited : Plain;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Dims a revealed hidden category so its state is obvious at a glance.</summary>
public sealed class HiddenOpacityConverter : IValueConverter
{
    public static HiddenOpacityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.45 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class HideGlyphConverter : IValueConverter
{
    public static HideGlyphConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "◉" : "✕";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ShowHiddenLabelConverter : IValueConverter
{
    public static ShowHiddenLabelConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Hide hidden categories" : "Show hidden categories";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Binds an enum to a ComboBox SelectedIndex.
/// </summary>
/// <remarks>
/// Relies on the combo's items being declared in the enum's own order, which
/// the views do deliberately so the two cannot drift apart unnoticed.
/// </remarks>
public sealed class EnumIndexConverter : IValueConverter
{
    public static EnumIndexConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? 0 : System.Convert.ToInt32(value, CultureInfo.InvariantCulture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int index)
        {
            return null;
        }

        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        return enumType.IsEnum && Enum.IsDefined(enumType, index)
            ? Enum.ToObject(enumType, index)
            : null;
    }
}

/// <summary>Play or pause glyph, driven by the engine's own state.</summary>
public sealed class PlayPauseGlyphConverter : IValueConverter
{
    public static PlayPauseGlyphConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PlaybackState.Playing or PlaybackState.Buffering ? "⏸" : "▶";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StateLabelConverter : IValueConverter
{
    public static StateLabelConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            PlaybackState.Opening => "Connecting...",
            PlaybackState.Buffering => "Buffering...",
            PlaybackState.Playing => string.Empty,
            PlaybackState.Paused => "Paused",
            PlaybackState.Ended => "Finished",
            PlaybackState.Error => "Error",
            _ => string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Greys out a favorite the current catalog no longer contains.</summary>
public sealed class AvailabilityOpacityConverter : IValueConverter
{
    public static AvailabilityOpacityConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string context && context == "Unavailable" ? 0.4 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Record button caption, which doubles as the stop control.</summary>
public sealed class RecordLabelConverter : IValueConverter
{
    public static RecordLabelConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "■ Stop recording" : "● Record";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RecordBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Recording = new(Color.Parse("#FF5B6E"));
    private static readonly SolidColorBrush Idle = new(Colors.White);

    public static RecordBrushConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Recording : Idle;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
