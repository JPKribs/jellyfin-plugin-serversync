using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using JPKribs.Jellyfin.Base;
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

    /// <summary>Where the .nfo for a link sits, beside it under the same name.</summary>
    /// <param name="linkPath">The link's .strm path.</param>
    /// <returns>The .nfo path.</returns>
    public static string PathFor(string linkPath) => Path.ChangeExtension(linkPath, ".nfo");

    /// <summary>Whether an .nfo at <paramref name="path"/> is one this plugin wrote for a link.</summary>
    /// <param name="path">The .nfo's path.</param>
    /// <returns><c>true</c> when this plugin wrote it.</returns>
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
    /// <param name="row">The link's row, whose companion files are read.</param>
    /// <returns><c>true</c> when the row records the .nfo.</returns>
    public static bool Recorded(Models.ContentSync.SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return !string.IsNullOrEmpty(row.LocalPath)
            && Names(row).Contains(Path.GetFileName(PathFor(row.LocalPath)), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Records on a link's row that its .nfo was written.</summary>
    /// <param name="row">The link's row, whose companion files gain the .nfo.</param>
    public static void Record(Models.ContentSync.SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.IsNullOrEmpty(row.LocalPath) && !Recorded(row))
        {
            row.CompanionFiles = string.Join(",", Names(row).Append(Path.GetFileName(PathFor(row.LocalPath))));
        }
    }

    /// <summary>Whether the user removed a link's .nfo after it was written.</summary>
    /// <param name="row">The link's row, which says whether the .nfo was written.</param>
    /// <returns><c>true</c> when it was removed.</returns>
    public static bool RemovedByUser(Models.ContentSync.SyncItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Recorded(row) && !File.Exists(PathFor(row.LocalPath!));
    }

    /// <summary>The companion file names a row records.</summary>
    private static string[] Names(Models.ContentSync.SyncItem row)
        => row.CompanionFiles?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? Array.Empty<string>();

    /// <summary>
    /// Writes the .nfo for a link, replacing only one this plugin wrote. Returns <c>false</c> when another .nfo
    /// is already there, which is left as it is.
    /// </summary>
    /// <param name="linkPath">The link's .strm path.</param>
    /// <param name="item">The source item the .nfo describes.</param>
    /// <returns><c>true</c> when the .nfo was written.</returns>
    public static bool Write(string linkPath, BaseItemDto item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var path = PathFor(linkPath);
        if (File.Exists(path) && !IsOurs(path))
        {
            return false;
        }

        AtomicFile.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(Build(item)));
        return true;
    }

    /// <summary>
    /// The .nfo for an item, in the form Jellyfin's own reader expects for its type. Each id keeps its name
    /// as the source spells it, since a lowered name would leave the item holding the id twice.
    /// </summary>
    /// <param name="item">The source item the .nfo describes.</param>
    /// <returns>The .nfo text.</returns>
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
                writer.WriteAttributeString("type", name);
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

    /// <summary>Writes an element when it has a value.</summary>
    private static void Element(XmlWriter writer, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            writer.WriteElementString(name, Clean(value));
        }
    }

    /// <summary>
    /// Drops what XML 1.0 cannot carry, most control characters and half of a surrogate pair, which a
    /// source's text may hold. Whole pairs, such as emoji, are kept.
    /// </summary>
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

    /// <summary>Writes a date the way an .nfo holds it.</summary>
    private static string? Date(DateTimeOffset? value)
        => value?.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A string writer that declares UTF-8, so the XML declaration names it.</summary>
    private sealed class StringWriterWithEncoding : StringWriter
    {
        public StringWriterWithEncoding(StringBuilder builder)
            : base(builder, CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
