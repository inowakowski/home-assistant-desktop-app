using Avalonia.Markup.Xaml;
using HADA.Presentation.Localization;

namespace HADA.App.Localization;

/// <summary>XAML access to <see cref="Loc"/>: <c>Text="{l:Tr Nav_Overview}"</c>.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
