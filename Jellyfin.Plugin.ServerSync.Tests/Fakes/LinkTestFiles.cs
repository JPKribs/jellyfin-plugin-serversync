using Jellyfin.Plugin.ServerSync.Services.Links;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.ServerSync.Tests.Fakes;

/// <summary>
/// Builds a real <see cref="LinkFiles"/> over stubs of the host and its paths, so a test writes and reads
/// links with a key kept in a folder of its own.
/// </summary>
internal static class LinkTestFiles
{
    /// <summary>The address every link URL built here starts with.</summary>
    public const string Address = "http://127.0.0.1:8096";

    /// <summary>
    /// Builds the link files service.
    /// </summary>
    /// <param name="dataPath">The folder the link key is kept in.</param>
    /// <returns>The service.</returns>
    public static LinkFiles Create(string dataPath)
    {
        var (host, hostStub) = InterfaceStub.Create<IServerApplicationHost>();
        hostStub.Answer("get_HttpPort", _ => 8096);
        hostStub.Answer(nameof(IServerApplicationHost.GetLocalApiUrl), args => $"{args[1]}://{args[0]}:{args[2]}");
        var (paths, pathsStub) = InterfaceStub.Create<IApplicationPaths>();
        pathsStub.Answer("get_DataPath", _ => dataPath);
        return new LinkFiles(host, paths);
    }
}
