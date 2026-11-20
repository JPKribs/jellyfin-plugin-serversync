using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Localization;

/// <summary>
/// Every key a page in Web/ names has a string, in Localization/en-us.json or as a declared Jellyfin string.
/// True: each data-str attribute and each tr, format, plural, and tabName call finds its text.
/// False: a page names a key nothing defines, so it would show the key in place of the words.
/// </summary>
public class TranslationKeyTests
{
    // Markup attributes, and the first literal key of a tr, format, or tabName call.
    private static readonly Regex KeyUse = new(
        @"data-str(?:-title|-label|-placeholder)?=""([A-Za-z0-9_.]+)""|\b(?:tr|format|tabName)\(\s*'([A-Za-z0-9_.]+)'",
        RegexOptions.Compiled);

    // The two keys of a plural call, which name the one and the many forms after the count.
    private static readonly Regex PluralCall = new(@"\bplural\([^']*'([A-Za-z0-9_.]+)'\s*,\s*'([A-Za-z0-9_.]+)'", RegexOptions.Compiled);

    private static readonly Regex Declared = new(@"<JellyfinString Include=""([^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void EveryKeyUsedInWeb_HasAString()
    {
        var project = ProjectFolder();
        var known = new HashSet<string>(StringComparer.Ordinal);
        using (var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "Localization", "en-us.json"))))
        {
            known.UnionWith(json.RootElement.EnumerateObject().Select(p => p.Name));
        }

        known.UnionWith(Declared.Matches(File.ReadAllText(Path.Combine(project, "Jellyfin.Plugin.ServerSync.csproj"))).Select(m => m.Groups[1].Value));

        var missing = new List<string>();
        var used = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(project, "Web")).Where(f => f.EndsWith(".html", StringComparison.Ordinal) || f.EndsWith(".js", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            var keys = KeyUse.Matches(text).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
                .Concat(PluralCall.Matches(text).SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }));
            foreach (var key in keys)
            {
                used++;
                if (!known.Contains(key))
                {
                    missing.Add(Path.GetFileName(file) + ": " + key);
                }
            }
        }

        Assert.True(used > 0, "No page names a key, so the patterns no longer match the pages.");
        Assert.True(missing.Count == 0, "Keys with no string:\n" + string.Join('\n', missing.Distinct()));
    }

    /// <summary>
    /// Finds the plugin's project folder from this file's own path.
    /// </summary>
    private static string ProjectFolder([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "Jellyfin.Plugin.ServerSync"));
}
