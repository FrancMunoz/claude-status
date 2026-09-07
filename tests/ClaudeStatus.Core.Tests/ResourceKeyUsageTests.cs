using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Every key the source refers to must exist, and every key that exists must be used.
/// </summary>
/// <remarks>
/// <para>
/// This is the compile-time check a string-keyed resource system throws away.
/// Without it, a typo in <c>L[Detials_Heading]</c> or a key renamed in the .resx
/// but not in the AXAML shows up as raw identifier text in the interface, and
/// only if someone happens to open that window.
/// </para>
/// <para>
/// It scans source files rather than using reflection because the AXAML keys
/// exist only as text inside binding expressions - there is nothing compiled to
/// reflect over.
/// </para>
/// </remarks>
public class ResourceKeyUsageTests
{
    /// <summary>
    /// Keys that are legitimately never named literally in the source.
    /// </summary>
    /// <remarks>
    /// The <c>Credential_*</c> and <c>CredentialTest_*</c> families are produced by
    /// switch expressions that return the key, and <c>Autostart_Error_*</c> by throw
    /// sites; the scan below sees those. This list is for anything a future change
    /// builds by concatenation, which the scan cannot follow.
    /// </remarks>
    private static readonly string[] ExpectedUnreferenced = [];

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ClaudeStatus.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.SkipWhen(directory is null, "Running outside the repository; source files unavailable.");
        return directory!.FullName;
    }

    private static HashSet<string> DeclaredKeys()
    {
        string path = Path.Combine(
            RepositoryRoot(), "src", "ClaudeStatus.Core", "Localization", "Strings.resx");

        return XDocument.Load(path).Root!
            .Elements("data")
            .Select(element => element.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Every key-shaped literal in the source, with where it was found.
    /// </summary>
    /// <remarks>
    /// Matches our key convention - <c>Area_Thing</c> in double quotes for C#, or
    /// bare inside an indexer binding for AXAML. Anything not shaped like a key is
    /// ignored, which is why the convention is worth keeping to.
    /// </remarks>
    private static List<(string Key, string File)> ReferencedKeys()
    {
        string root = Path.Combine(RepositoryRoot(), "src");
        var found = new List<(string, string)>();

        // "Tray_Quit" in C#, and L[Tray_Quit] in an AXAML compiled binding.
        var csharp = new Regex("\"([A-Z][A-Za-z]+(?:_[A-Za-z0-9]+)+)\"");
        var axaml = new Regex(@"\bL\[([A-Za-z0-9_]+)\]");

        // Comments explaining the binding syntax contain examples that look exactly
        // like real usages, so they have to go before the scan, not after.
        var xmlComment = new Regex("<!--.*?-->", RegexOptions.Singleline);

        foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            string extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".axaml")
                || file.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            string name = Path.GetRelativePath(root, file);

            if (extension == ".axaml")
            {
                text = xmlComment.Replace(text, string.Empty);
            }

            foreach (Match match in (extension == ".axaml" ? axaml : csharp).Matches(text))
            {
                found.Add((match.Groups[1].Value, name));
            }
        }

        return found;
    }

    [Fact]
    public void Every_key_named_in_an_AXAML_binding_exists_in_the_resources()
    {
        // The one that actually bites: a view binds L[Something_Wrong] and the
        // window shows the identifier. Nothing else catches it.
        HashSet<string> declared = DeclaredKeys();

        IEnumerable<string> missing = ReferencedKeys()
            .Where(pair => pair.File.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(pair => !declared.Contains(pair.Key))
            .Select(pair => $"{pair.Key} ({pair.File})")
            .Distinct(StringComparer.Ordinal);

        missing.Should().BeEmpty("every L[Key] binding must resolve to a real resource");
    }

    [Fact]
    public void Every_key_the_resources_declare_is_used_somewhere()
    {
        // Catches the other direction: a key kept alive in five languages long
        // after the control that showed it was deleted. Translators pay for those.
        HashSet<string> referenced = ReferencedKeys()
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);

        IEnumerable<string> unused = DeclaredKeys()
            .Where(key => !referenced.Contains(key))
            .Except(ExpectedUnreferenced, StringComparer.Ordinal);

        unused.Should().BeEmpty(
            "an unused key is dead weight in five files; delete it or start using it");
    }
}
