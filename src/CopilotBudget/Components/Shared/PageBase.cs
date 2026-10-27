using CopilotBudget.Services;
using Microsoft.AspNetCore.Components;

namespace CopilotBudget.Components.Shared;

/// <summary>
/// Base for dashboard pages. Loads the session, then reloads whenever someone changes budgets,
/// requests or organizations in any browser session.
/// </summary>
public abstract class PageBase : ComponentBase, IDisposable
{
    [Inject] protected UserSession Session { get; set; } = default!;
    [Inject] protected BudgetService Budgets { get; set; } = default!;
    [Inject] private ChangeNotifier Notifier { get; set; } = default!;

    protected bool Loading { get; private set; } = true;
    protected OpResult? Result { get; set; }

    protected override async Task OnInitializedAsync()
    {
        await Session.EnsureLoadedAsync();
        Session.Changed += OnSessionChanged;
        Notifier.Changed += OnDataChanged;
        await ReloadAsync();
    }

    /// <summary>Load page data. Only called for people who can see the dashboard.</summary>
    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected async Task ReloadAsync()
    {
        if (Session.InUserbase) await LoadAsync();
        Loading = false;
    }

    /// <summary>Show the outcome of an action and refresh the page data.</summary>
    protected async Task ShowAsync(OpResult result)
    {
        Result = result;
        await ReloadAsync();
    }

    private void OnSessionChanged() => _ = InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    private Task OnDataChanged() => InvokeAsync(async () =>
    {
        await ReloadAsync();
        StateHasChanged();
    });

    public virtual void Dispose()
    {
        Session.Changed -= OnSessionChanged;
        Notifier.Changed -= OnDataChanged;
        GC.SuppressFinalize(this);
    }
}
