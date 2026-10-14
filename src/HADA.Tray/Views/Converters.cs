using System.Globalization;
using System.Windows.Data;
using HADA.Presentation;
using Wpf.Ui.Controls;

namespace HADA.Tray.Views;

/// <summary>The WPF-UI icon for a <see cref="Glyph"/>: the regular one of 24 pixels of the same name.</summary>
public sealed class GlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Glyph glyph && Enum.TryParse<SymbolRegular>(glyph + "24", out var symbol) ? symbol : SymbolRegular.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The WPF-UI InfoBar severity for a <see cref="Severity"/>.</summary>
public sealed class SeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Severity.Success => InfoBarSeverity.Success,
        Severity.Warning => InfoBarSeverity.Warning,
        Severity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
