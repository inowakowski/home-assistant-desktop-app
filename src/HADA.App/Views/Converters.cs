using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HADA.Presentation;
using HADA.Presentation.ViewModels;
using Microsoft.Extensions.Logging;
using FluentIconKind = FluentIcons.Common.Icon;

namespace HADA.App.Views;

/// <summary>The Fluent icon for a <see cref="Glyph"/>, which is named after it.</summary>
public sealed class GlyphConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Glyph glyph && Enum.TryParse<FluentIconKind>(glyph.ToString(), out var icon) ? icon : FluentIconKind.Circle;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The colour of a status dot: green, amber, red, or grey for nothing to say.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly IBrush Success = new SolidColorBrush(Color.Parse("#6CCB5F"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#FCE100"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#FF99A4"));
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#9D9D9D"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusKind.Success => Success,
        StatusKind.Warning => Warning,
        StatusKind.Error => Error,
        _ => Neutral,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// The fill or the edge of a message by its <see cref="Severity"/>: <c>ConverterParameter=edge</c> for the edge.
/// Tints that read on light and dark alike.
/// </summary>
public sealed class SeverityBrushConverter : IValueConverter
{
    private static readonly IBrush SuccessFill = new SolidColorBrush(Color.Parse("#266CCB5F"));
    private static readonly IBrush SuccessEdge = new SolidColorBrush(Color.Parse("#806CCB5F"));
    private static readonly IBrush WarningFill = new SolidColorBrush(Color.Parse("#26FCE100"));
    private static readonly IBrush WarningEdge = new SolidColorBrush(Color.Parse("#99C9A800"));
    private static readonly IBrush ErrorFill = new SolidColorBrush(Color.Parse("#26FF6B6B"));
    private static readonly IBrush ErrorEdge = new SolidColorBrush(Color.Parse("#99E05555"));
    private static readonly IBrush InfoFill = new SolidColorBrush(Color.Parse("#264CC2FF"));
    private static readonly IBrush InfoEdge = new SolidColorBrush(Color.Parse("#804CA6E0"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var edge = parameter as string == "edge";
        return value switch
        {
            Severity.Success => edge ? SuccessEdge : SuccessFill,
            Severity.Warning => edge ? WarningEdge : WarningFill,
            Severity.Error => edge ? ErrorEdge : ErrorFill,
            _ => edge ? InfoEdge : InfoFill,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The badge behind a log entry's level: neutral, then the colours of messages as the level rises.</summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    private static readonly IBrush Neutral = new SolidColorBrush(Color.Parse("#26808080"));
    private static readonly IBrush Information = new SolidColorBrush(Color.Parse("#334CC2FF"));
    private static readonly IBrush Warning = new SolidColorBrush(Color.Parse("#40FCE100"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#40FF6B6B"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Information => Information,
        LogLevel.Warning => Warning,
        LogLevel.Error or LogLevel.Critical => Error,
        _ => Neutral,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when a value equals the parameter, e.g. a step's number; for showing what belongs to it.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
