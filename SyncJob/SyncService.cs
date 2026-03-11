using Microsoft.Extensions.Logging;

namespace SyncJob;

/// <summary>
/// Public entry point for the sync library.
/// Provides manual trigger, scheduler, and concurrency guard.
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly SyncOrchestrator _orchestrator;
    private readonly ILogger<SyncService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _schedulerCts;
    private Task? _schedulerTask;

    public bool IsRunning => _gate.CurrentCount == 0;

    public event EventHandler<SyncResult>? SyncCompleted;

    internal SyncService(SyncOrchestrator orchestrator, ILogger<SyncService> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // Manual trigger
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs a sync immediately. Returns the result.
    /// If a sync is already running, returns immediately with a no-op result.
    /// </summary>
    public async Task<SyncResult> RunAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            _logger.LogInformation("Sync already in progress — skipping duplicate trigger.");
            return new SyncResult
            {
                Success = true,
                CompletedAt = DateTime.UtcNow,
                FatalError = "Sync already in progress.",
            };
        }

        try
        {
            _logger.LogInformation("Sync run starting.");
            var result = await RunCoreAsync(ct);
            _logger.LogInformation(
                "Sync run complete. Success={Success} Pushed={Pushed} Errors={Errors}",
                result.Success, result.PushedToShopify, result.Errors.Count);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // -------------------------------------------------------------------------
    // Scheduler
    // -------------------------------------------------------------------------

    /// <summary>Starts the background scheduler. No-op if already running.</summary>
    public void StartScheduler(TimeSpan interval)
    {
        if (_schedulerTask is { IsCompleted: false })
        {
            _logger.LogWarning("Scheduler is already running.");
            return;
        }

        _schedulerCts = new CancellationTokenSource();
        var token = _schedulerCts.Token;

        _schedulerTask = Task.Run(async () =>
        {
            _logger.LogInformation("Scheduler started with interval {Interval}.", interval);
            using var timer = new PeriodicTimer(interval);

            while (await timer.WaitForNextTickAsync(token))
            {
                var result = await RunAsync(token);
                SyncCompleted?.Invoke(this, result);
            }
        }, token);
    }

    /// <summary>Stops the background scheduler. Waits for any in-progress run to finish.</summary>
    public void StopScheduler()
    {
        if (_schedulerCts is null) return;

        _schedulerCts.Cancel();
        try { _schedulerTask?.Wait(); }
        catch (OperationCanceledException) { }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }

        _schedulerCts.Dispose();
        _schedulerCts = null;
        _schedulerTask = null;

        _logger.LogInformation("Scheduler stopped.");
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private async Task<SyncResult> RunCoreAsync(CancellationToken ct)
    {
        try
        {
            return await _orchestrator.RunAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return SyncResult.Fatal("Sync was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled exception during sync run.");
            return SyncResult.Fatal(ex.Message);
        }
    }

    public void Dispose()
    {
        StopScheduler();
        _gate.Dispose();
    }
}
