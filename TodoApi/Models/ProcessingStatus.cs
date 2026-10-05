namespace TodoApi.Models;

/// <summary>
/// Where a todo is in the asynchronous workflow. Owned by the Todo API: the Go worker only *reports* transitions
/// and this service decides whether they are valid and persists them.
///
/// QUEUED -> PROCESSING -> WAITING_RELEASE -> PERSISTING -> COMPLETED
///               \------------- FAILED (from any non-terminal state)
/// </summary>
public enum ProcessingStatus
{
    Queued,
    Processing,
    WaitingRelease,
    Persisting,
    Completed,
    Failed,
}

public static class ProcessingStatuses
{
    public static string ToWire(this ProcessingStatus s) => s switch
    {
        ProcessingStatus.Queued => "QUEUED",
        ProcessingStatus.Processing => "PROCESSING",
        ProcessingStatus.WaitingRelease => "WAITING_RELEASE",
        ProcessingStatus.Persisting => "PERSISTING",
        ProcessingStatus.Completed => "COMPLETED",
        ProcessingStatus.Failed => "FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    public static bool TryParse(string? wire, out ProcessingStatus status)
    {
        status = wire switch
        {
            "QUEUED" => ProcessingStatus.Queued,
            "PROCESSING" => ProcessingStatus.Processing,
            "WAITING_RELEASE" => ProcessingStatus.WaitingRelease,
            "PERSISTING" => ProcessingStatus.Persisting,
            "COMPLETED" => ProcessingStatus.Completed,
            "FAILED" => ProcessingStatus.Failed,
            _ => (ProcessingStatus)(-1),
        };
        return Enum.IsDefined(status);
    }

    public static bool IsTerminal(this ProcessingStatus s) => s is ProcessingStatus.Completed or ProcessingStatus.Failed;

    /// <summary>
    /// Statuses from which a report of <paramref name="target"/> is accepted. The rule is "move forward only":
    /// a late or duplicated message can never move a todo backwards, and skipping a step (e.g. the PROCESSING
    /// message arrived after WAITING_RELEASE) is tolerated, which makes the consumer order-insensitive.
    /// Terminal states never change by report; FAILED can be entered from any non-terminal state.
    /// </summary>
    public static ProcessingStatus[] AcceptedFrom(ProcessingStatus target) => target switch
    {
        ProcessingStatus.Failed => [ProcessingStatus.Queued, ProcessingStatus.Processing, ProcessingStatus.WaitingRelease, ProcessingStatus.Persisting],
        ProcessingStatus.Queued => [], // only an admin retry (explicit API call) re-queues a todo
        _ => Enum.GetValues<ProcessingStatus>().Where(from => from != ProcessingStatus.Failed && !from.IsTerminal() && from < target).ToArray(),
    };
}
