namespace CopilotBudget.Services;

/// <summary>
/// When the app last heard from GitHub, shown in the top bar. Pages update live over the Blazor Server connection
/// (SignalR): every change, and every sync, pushes fresh numbers to everyone who has the site open.
/// </summary>
public class LiveStatus
{
    public DateTime? LastSyncUtc { get; private set; }
    public string? LastSyncProblem { get; private set; }
    public DateTime? LastWriteBackUtc { get; private set; }
    public string? LastWriteBackProblem { get; private set; }

    public void SyncFinished(DateTime utc, string? problem)
    {
        LastSyncUtc = utc;
        LastSyncProblem = problem;
    }

    public void WriteBackFinished(DateTime utc, string? problem)
    {
        LastWriteBackUtc = utc;
        LastWriteBackProblem = problem;
    }
}
