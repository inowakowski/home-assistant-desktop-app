using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace HADA.Tests.Tray;

/// <summary>Guards the tray's translations: both languages complete, and no key used in code or XAML missing.</summary>
public partial class LocalizationTests
{
    [Fact]
    public void Polish_and_English_define_the_same_strings()
    {
        var english = Keys("Strings.resx");
        var polish = Keys("Strings.pl.resx");

        Assert.Empty(english.Except(polish));
        Assert.Empty(polish.Except(english));
    }

    [Fact]
    public void Every_string_used_by_the_tray_exists()
    {
        var defined = Keys("Strings.resx");
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            var pattern = file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ? XamlKey() : CodeKey();
            foreach (Match match in pattern.Matches(text))
            {
                used.Add(match.Groups["key"].Value);
            }
        }

        Assert.NotEmpty(used);
        Assert.Empty(used.Except(defined));
    }

    private static HashSet<string> Keys(string fileName) =>
        XDocument.Load(Path.Combine(TrayFolder(), "Localization", fileName)).Root!
            .Elements("data")
            .Select(data => (string)data.Attribute("name")!)
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles()
    {
        var excluded = new[] { $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}" };
        return Directory.EnumerateFiles(TrayFolder(), "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(file => !excluded.Any(file.Contains));
    }

    private static string TrayFolder([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", "..", "..", "src", "HADA.Tray"));

    [GeneratedRegex(@"\{l:Tr (?<key>\w+)\}")]
    private static partial Regex XamlKey();

    // Literal keys in code; dynamic ones such as $"EntityHint_{id}" are looked up with TryGet and may be missing.
    [GeneratedRegex(@"""(?<key>(?:App|Nav|Common|Tray|Overview|Card|Status|State|Column|Entity|Source|Connections|Settings|Mqtt|Ha|Field|Placeholder|Test|Save|Validation|Error|Entities|Logs)_\w+)""")]
    private static partial Regex CodeKey();
}
