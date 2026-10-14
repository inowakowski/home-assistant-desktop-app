using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using HADA.Presentation;
using HADA.Presentation.ViewModels;
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
