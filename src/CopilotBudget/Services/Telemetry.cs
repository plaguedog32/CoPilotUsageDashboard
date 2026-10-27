using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using CopilotBudget.Data;
using Microsoft.EntityFrameworkCore;

namespace CopilotBudget.Services;

/// <summary>
/// Metrics the app reports through OpenTelemetry. With an Application Insights connection string they show up as
/// custom metrics, so Azure Monitor alert rules can fire on them.
/// </summary>
public static class Telemetry
{
    public const string Name = "CopilotBudget";
    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> ApiCalls = Meter.CreateCounter<long>("copilotbudget.api.calls", description: "Calls to GitHub and Microsoft Graph");
    public static readonly Counter<long> ApiFailures = Meter.CreateCounter<long>("copilotbudget.api.failures", description: "Failed calls to GitHub and Microsoft Graph");
    public static readonly Counter<long> SignInFailures = Meter.CreateCounter<long>("copilotbudget.signin.failures", description: "Failed or refused sign-ins");
    public static readonly Counter<long> AlertsRaised = Meter.CreateCounter<long>("copilotbudget.alerts.raised", description: "New alerts");
    public static readonly Counter<long> BudgetWriteFailures = Meter.CreateCounter<long>("copilotbudget.writeback.failures", description: "GitHub budget writes that failed or need reconciling");
}

/// <summary>Queues every outside API call for logging, without slowing the call down.</summary>
public class ApiCallRecorder
{
    private readonly Channel<ApiCall> _queue = Channel.CreateBounded<ApiCall>(new BoundedChannelOptions(5000)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    public ChannelReader<ApiCall> Reader => _queue.Reader;

    public void Record(ApiCall call)
    {
        _queue.Writer.TryWrite(call);
        var tags = new TagList { { "service", call.Service }, { "status", call.Status } };
        Telemetry.ApiCalls.Add(1, tags);
        if (!call.Succeeded) Telemetry.ApiFailures.Add(1, tags);
    }
}

/// <summary>
/// HTTP handler on the GitHub and Graph clients: records method, path, status, time taken and GitHub's remaining
/// rate limit for every call. Never records headers or bodies of successful calls (tokens stay out of the log).
/// </summary>
public class ApiCallLoggingHandler(ApiCallRecorder recorder, string service) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var call = new ApiCall
        {
            Service = service,
            Method = request.Method.Method,
            Path = Truncate(request.RequestUri?.PathAndQuery ?? "", 1000)!,
        };
        try
        {
            var response = await base.SendAsync(request, ct);
            call.Status = (int)response.StatusCode;
            if (response.Headers.TryGetValues("x-ratelimit-remaining", out var values) && int.TryParse(values.FirstOrDefault(), out var left))
                call.RateLimitRemaining = left;
            if (!response.IsSuccessStatusCode && response.Content is not null)
            {
                await response.Content.LoadIntoBufferAsync(ct);
                call.Error = Truncate(await response.Content.ReadAsStringAsync(ct), 500);
            }
            return response;
        }
        catch (Exception ex)
        {
            call.Error = Truncate(ex.Message, 500);
            throw;
        }
        finally
        {
            call.DurationMs = (int)watch.ElapsedMilliseconds;
            recorder.Record(call);
        }
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}

/// <summary>Writes queued API calls to the database in batches, and prunes old ones.</summary>
public class ApiCallWriter(ApiCallRecorder recorder, IDbContextFactory<AppDbContext> dbFactory, IConfiguration configuration, ILogger<ApiCallWriter> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPrune = DateTime.MinValue;
        var batch = new List<ApiCall>();
        try
        {
            while (await recorder.Reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < 200 && recorder.Reader.TryRead(out var call)) batch.Add(call);
                try
                {
                    await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                    db.ApiCalls.AddRange(batch);
                    await db.SaveChangesAsync(stoppingToken);

                    if (DateTime.UtcNow - lastPrune > TimeSpan.FromHours(6))
                    {
                        lastPrune = DateTime.UtcNow;
                        var days = Math.Max(1, configuration.GetValue("App:ApiLogRetentionDays", 30));
                        var cutoff = DateTime.UtcNow.AddDays(-days);
                        await db.ApiCalls.Where(c => c.CreatedUtc < cutoff).ExecuteDeleteAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Couldn't save {Count} API call log entries", batch.Count);
                }
                batch.Clear();
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); // Let a burst of calls batch up.
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
