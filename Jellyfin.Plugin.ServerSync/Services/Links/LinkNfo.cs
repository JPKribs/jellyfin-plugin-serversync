using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Jellyfin.Sdk.Generated.Models;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>
/// The .nfo written beside a link, from the source's details for the item, so Jellyfin knows exactly
/// what the link is without guessing from its name: title, year, plot, ids, and for an episode its show,
/// season, and number. Only an .nfo this plugin wrote is ever replaced, and it is told apart by a marker.
/// </summary>
public static class LinkNfo
{
    /// <summary>The marker that says an .nfo was written by Server Sync for a link.</summary>
    public const string Marker = "Written by Server Sync for a link";

    /// <summary>The fields to read from the source so the .nfo can be written.</summary>
    public static readonly ItemFields[] Fields =
    {
        ItemFields.Path, ItemFields.Overview, ItemFields.ProviderIds, ItemFields.Genres, ItemFields.Studios,
        ItemFields.OriginalTitle, ItemFields.Tags, ItemFields.Taglines
    };

    /// <summary>Where the .nfo for a link sits: beside it, under the same name.</summary>
    public static string PathFor(string linkPath) => Path.ChangeExtension(linkPath, ".nfo");

    /// <summary>Whether an .nfo at <paramref name="path"/> is one this plugin wrote for a link.</summary>
    public static bool IsOurs(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadLines(path).Take(4).Any(l => l.Contains(Marker, StringComparison.Ordinal));
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a link's row records that its .nfo was written. A recorded .nfo that is gone was removed
    /// by the user, to hand the metadata to Jellyfin, and is not written again.
    /// </summary>
    public static bool Recorded(Models.ContentSync.SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !string.IsNullOrEmpty(row.LocalPath)
            && Names(row).Contains(Path.GetFileName(PathFor(row.LocalPath)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records on a link's row that its .nfo was written.</summary>
    public static void Record(Models.ContentSync.SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.IsNullOrEmpty(row.LocalPath) && !Recorded(row))
        {
            row.CompanionFiles = string.Join(",", Names(row).Append(Path.GetFileName(PathFor(row.LocalPath))));
        }
    }

    /// <summary>Whether the user removed a link's .nfo after it was written.</summary>
    public static bool RemovedByUser(Models.ContentSync.SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Recorded(row) && !File.Exists(PathFor(row.LocalPath!));
    }

    private static string[] Names(Models.ContentSync.SyncItem row)
        => row.CompanionFiles?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();

    /// <summary>
    /// Writes the .nfo for a link, replacing only one this plugin wrote. Returns false when another .nfo
    /// is already there, which is left as it is.
    /// </summary>
    public static bool Write(string linkPath, BaseItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var path = PathFor(linkPath);
        if (File.Exists(path) && !IsOurs(path))
        {
            return false;
        }

        var temp = path + ".serversync-tmp";
        File.WriteAllText(temp, Build(item), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return true;
    }

    /// <summary>The .nfo for an item, in the form Jellyfin's own reader expects for its type.</summary>
    public static string Build(BaseItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var root = item.Type switch
        {
            BaseItemDto_Type.Episode => "episodedetails",
            BaseItemDto_Type.MusicVideo => "musicvideo",
            _ => "movie"
        };

        var text = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false, CheckCharacters = false };
        using (var writer = XmlWriter.Create(new StringWriterWithEncoding(text), settings))
        {
            writer.WriteStartDocument(standalone: true);
            writer.WriteComment($" {Marker}. It is replaced when the link is written again. Delete this line to keep your own edits. ");
            writer.WriteStartElement(root);
            Element(writer, "title", item.Name);
            Element(writer, "originaltitle", item.OriginalTitle);
            if (item.Type == BaseItemDto_Type.Episode)
            {
                Element(writer, "showtitle", item.SeriesName);
                Element(writer, "season", item.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture));
                Element(writer, "episode", item.IndexNumber?.ToString(CultureInfo.InvariantCulture));
                Element(writer, "aired", Date(item.PremiereDate));
            }
            else
            {
                Element(writer, "premiered", Date(item.PremiereDate));
            }

            Element(writer, "year", item.ProductionYear?.ToString(CultureInfo.InvariantCulture));
            Element(writer, "plot", item.Overview);
            Element(writer, "tagline", item.Taglines?.FirstOrDefault());
            Element(writer, "mpaa", item.OfficialRating);
            if (item.RunTimeTicks is > 0 and var ticks)
            {
                Element(writer, "runtime", ((long)Math.Round(ticks / (double)TimeSpan.TicksPerMinute)).ToString(CultureInfo.InvariantCulture));
            }

            foreach (var genre in item.Genres ?? Enumerable.Empty<string>())
            {
                Element(writer, "genre", genre);
            }

            foreach (var studio in item.Studios?.Select(s => s.Name) ?? Enumerable.Empty<string?>())
            {
                Element(writer, "studio", studio);
            }

            foreach (var tag in item.Tags ?? Enumerable.Empty<string>())
            {
                Element(writer, "tag", tag);
            }

            var ids = item.ProviderIds?.AdditionalData;
            var first = true;
            foreach (var (name, value) in ids?.OrderBy(p => p.Key, StringComparer.Ordinal) ?? Enumerable.Empty<System.Collections.Generic.KeyValuePair<string, object>>())
            {
                if (value?.ToString() is not { Length: > 0 } id)
                {
                    continue;
                }

                writer.WriteStartElement("uniqueid");
                writer.WriteAttributeString("type", name.ToLowerInvariant());
                if (first)
                {
                    writer.WriteAttributeString("default", "true");
                    first = false;
                }

                writer.WriteString(Clean(id));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        return text.ToString();
    }

    private static void Element(XmlWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteElementString(name, Clean(value));
        }
    }

    // XML 1.0 cannot carry most control characters or half of a surrogate pair, and a source's text may
    // hold either. Whole pairs, such as emoji, are kept.
    private static string Clean(string value)
    {
        var text = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                text.Append(c).Append(value[++i]);
            }
            else if (!char.IsSurrogate(c) && XmlConvert.IsXmlChar(c))
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    private static string? Date(DateTimeOffset? value)
        => value?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed class StringWriterWithEncoding : StringWriter
    {
        public StringWriterWithEncoding(StringBuilder builder)
            : base(builder, CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
