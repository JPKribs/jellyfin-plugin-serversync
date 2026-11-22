using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ServerSync.Services.Links;

/// <summary>Asks the servers that hold a link's file whether a user here may play it.</summary>
public interface ILinkAccess
{
    /// <summary>
    /// Asks the link's own server, then the others that hold the same file, until one allows it.
    /// A null user is this server's own media probe.
    /// </summary>
    /// <param name="link">The link to find a route for.</param>
    /// <param name="userId">The user here, or null for the probe.</param>
    /// <param name="skip">Server entries not to ask, such as one that just failed.</param>
    /// <param name="cancellationToken">Cancels the asking.</param>
    /// <returns>The route to stream from, or why none would.</returns>
    Task<LinkChoice> ChooseAsync(LinkEntry link, Guid? userId, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the same servers as <see cref="ChooseAsync"/> whether a user here may download the file, which
    /// also needs download permission on the server that sends it.
    /// </summary>
    /// <param name="link">The link to find a route for.</param>
    /// <param name="userId">The user here who downloads it.</param>
    /// <param name="skip">Server entries not to ask, such as one that just failed.</param>
    /// <param name="cancellationToken">Cancels the asking.</param>
    /// <returns>The route to download from, or why none would.</returns>
    Task<LinkChoice> ChooseForDownloadAsync(LinkEntry link, Guid userId, IReadOnlyCollection<string>? skip, CancellationToken cancellationToken);
}
