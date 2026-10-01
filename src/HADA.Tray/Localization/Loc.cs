using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace HADA.Tray.Localization;

/// <summary>UI strings from <c>Strings.resx</c>: Polish when Windows' display language is Polish, English otherwise.</summary>
public static class Loc
{
    private static readonly ResourceManager Strings = new("HADA.Tray.Localization.Strings", typeof(Loc).Assembly);

    public static CultureInfo Culture { get; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl"
            ? CultureInfo.GetCultureInfo("pl-PL")
            : CultureInfo.GetCultureInfo("en-US");

    /// <summary>Returns the string, or <c>[key]</c> so a missing translation is visible instead of blank.</summary>
    public static string Get(string key) => TryGet(key) ?? $"[{key}]";

    public static string? TryGet(string key) => Strings.GetString(key, Culture);

    public static string Format(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments);
}

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
