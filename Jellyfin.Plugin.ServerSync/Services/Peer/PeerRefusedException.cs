using System;
using System.Net;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// A peer answered a Server Sync request with an HTTP error. The status is kept so callers can tell a
/// key that is not an administrator's, which is 401 or 403 and means the peer's Server Sync endpoints
/// are off limits to it, from a peer that is simply down.
/// </summary>
public sealed class PeerRefusedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerRefusedException"/> class.
    /// </summary>
    /// <param name="statusCode">The HTTP status the peer answered with.</param>
    /// <param name="message">The message.</param>
    public PeerRefusedException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerRefusedException"/> class.
    /// </summary>
    public PeerRefusedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerRefusedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    public PeerRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerRefusedException"/> class.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The inner exception.</param>
    public PeerRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the HTTP status the peer answered with.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets a value indicating whether the peer refused the key itself rather than the request.</summary>
    public bool KeyRefused => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
