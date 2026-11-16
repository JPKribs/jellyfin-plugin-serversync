using Microsoft.Kiota.Abstractions;

namespace Jellyfin.Plugin.ServerSync.Services.Clients;

/// <summary>
/// Marks a request whose answer a large source takes minutes to start, such as the list of every person.
/// <see cref="SlowAnswerHandler"/> reads it and sends the request once, with thirty minutes to answer.
/// </summary>
public sealed class SlowAnswerOption : IRequestOption
{
}
