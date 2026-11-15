using System;
using Jellyfin.Plugin.ServerSync.Tasks.Common;
using Xunit;

namespace Jellyfin.Plugin.ServerSync.Tests.Tasks.Common;

/// <summary>
/// A busy sync type is reported as its own exception type that carries the reason it was given.
/// </summary>
public sealed class ModuleBusyExceptionTests
{
    /// <summary>
    /// The parameterless form names the scheduled run as the reason.
    /// True: a caller that gives no reason still stores a message that says why the row was put off.
    /// False: a put off hint carries an empty or generic framework message.
    /// </summary>
    [Fact]
    public void Constructor_NoArguments_NamesTheScheduledRun()
    {
        var exception = new ModuleBusyException();

        Assert.Equal("the module is busy with a scheduled run", exception.Message);
        Assert.Null(exception.InnerException);
    }

    /// <summary>
    /// A given message is kept as written.
    /// True: the task name the thrower supplies reaches the caller.
    /// False: the caller cannot tell which sync type was busy.
    /// </summary>
    [Fact]
    public void Constructor_Message_KeepsIt()
    {
        var exception = new ModuleBusyException("Sync History is running");

        Assert.Equal("Sync History is running", exception.Message);
    }

    /// <summary>
    /// The inner exception is kept beside the message.
    /// True: the cause of a busy state stays attached for the log.
    /// False: wrapping a failure loses what led to it.
    /// </summary>
    [Fact]
    public void Constructor_MessageAndInner_KeepsBoth()
    {
        var inner = new TimeoutException("waited");

        var exception = new ModuleBusyException("busy", inner);

        Assert.Equal("busy", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    /// <summary>
    /// The type is a plain exception and not a cancellation.
    /// True: a caller that lets cancellation through still catches a busy sync type.
    /// False: a busy sync type would be mistaken for a cancelled run and dropped.
    /// </summary>
    [Fact]
    public void Type_IsNotACancellation()
    {
        Exception exception = new ModuleBusyException();

        Assert.IsNotAssignableFrom<OperationCanceledException>(exception);
    }
}
