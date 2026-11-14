using System.Xml.Serialization;

namespace Jellyfin.Plugin.ServerSync.Models.Configuration;

/// <summary>
/// What this server does with a configured server. The settings file stores them as Scan, Send, and Both.
/// </summary>
public enum ServerMode
{
    /// <summary>This server reads from the other on a schedule. The other needs no plugin.</summary>
    [XmlEnum("Scan")]
    Pull = 0,

    /// <summary>This server tells the other about changes as they happen, and the other pulls them. Needs the plugin there.</summary>
    [XmlEnum("Send")]
    Push = 1,

    /// <summary>Pull and Push together, for a pool of equals.</summary>
    [XmlEnum("Both")]
    Sync = 2
}
