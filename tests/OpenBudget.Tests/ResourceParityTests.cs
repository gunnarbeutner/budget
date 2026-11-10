using System.Xml.Linq;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Every English .resx has a German twin with exactly the same keys, so no screen falls back to English by accident.</summary>
public sealed class ResourceParityTests
{
    private static string ResourcesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "budget.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir.FullName, "src", "OpenBudget", "Resources");
    }

    private static HashSet<string> Keys(string path) =>
        XDocument.Load(path).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_resource_has_the_same_keys_in_english_and_german()
    {
        var problems = new List<string>();
        foreach (var english in Directory.EnumerateFiles(ResourcesDir(), "*.resx", SearchOption.AllDirectories).Where(f => !f.EndsWith(".de.resx", StringComparison.Ordinal)))
        {
            var german = english[..^".resx".Length] + ".de.resx";
            if (!File.Exists(german)) { problems.Add($"{Path.GetFileName(english)}: no German file"); continue; }
            var en = Keys(english);
            var de = Keys(german);
            foreach (var k in en.Except(de, StringComparer.Ordinal)) problems.Add($"{Path.GetFileName(english)}: '{k}' missing in German");
            foreach (var k in de.Except(en, StringComparer.Ordinal)) problems.Add($"{Path.GetFileName(german)}: '{k}' missing in English");
        }
        Assert.Empty(problems);
    }
}
