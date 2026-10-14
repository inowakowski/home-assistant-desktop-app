using System.Windows.Markup;
using HADA.Presentation.Localization;

namespace HADA.Tray.Localization;

/// <summary>XAML access to <see cref="Loc"/>: <c>Text="{l:Tr Nav_Overview}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
