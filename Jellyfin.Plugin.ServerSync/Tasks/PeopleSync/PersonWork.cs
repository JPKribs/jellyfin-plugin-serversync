using Jellyfin.Plugin.ServerSync.Tasks.Common;
using SdkBaseItemDto = Jellyfin.Sdk.Generated.Models.BaseItemDto;

namespace Jellyfin.Plugin.ServerSync.Tasks.PeopleSync;

/// <summary>
/// Source side work item for the People refresh task, one source person and the server it came from.
/// </summary>
/// <param name="Source">The server the person came from.</param>
/// <param name="Person">The person as the source reports them.</param>
public sealed record PersonWork(ScanSource Source, SdkBaseItemDto Person);
