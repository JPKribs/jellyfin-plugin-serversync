using System;
using System.Net;

namespace Jellyfin.Plugin.ServerSync.Services.Peer;

/// <summary>
/// A peer answered a Server Sync request with an HTTP error, whose status tells a refused API Key from a peer that is down.
/// </summary>
public sealed class PeerRefusedException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PeerRefusedException"/> class.
    /// </summary>
    /// <param name="statusCode">The HTTP status the peer answered with.</param>
    /// <param name="message">What the peer refused and why.</param>
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
    /// <param name="message">What the peer refused and why.</param>
    public PeerRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PeerRefusedException"/> class.
    /// </summary>
    /// <param name="message">What the peer refused and why.</param>
    /// <param name="innerException">The failure that led to this one.</param>
    public PeerRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the HTTP status the peer answered with.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets a value indicating whether the peer refused the API Key itself, with a 401 or 403, rather than the request.</summary>
    public bool KeyRefused => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}
