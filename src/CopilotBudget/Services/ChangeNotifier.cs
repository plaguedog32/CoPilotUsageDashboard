namespace CopilotBudget.Services;

/// <summary>
/// Lets open dashboards refresh when budgets change in another browser session.
/// Registered as a singleton; each Blazor circuit subscribes and unsubscribes.
/// </summary>
public class ChangeNotifier(ILogger<ChangeNotifier> logger)
{
    public event Func<Task>? Changed;

    /// <summary>Tells every subscriber something changed. Doesn't wait for them, so a slow tab can't stall the caller.</summary>
    public Task NotifyAsync()
    {
        var handlers = Changed?.GetInvocationList().Cast<Func<Task>>() ?? [];
        foreach (var handler in handlers)
        {
            _ = Task.Run(async () =>
            {
                try { await handler(); }
                catch (Exception ex) { logger.LogDebug(ex, "Change subscriber failed"); }
            });
        }
        return Task.CompletedTask;
    }
}
