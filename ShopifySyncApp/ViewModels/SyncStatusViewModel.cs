using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShopifySyncApp.Services;
using SyncHistory;
using SyncJob;

namespace ShopifySyncApp.ViewModels;

public partial class SyncStatusViewModel : ViewModelBase
{
    private readonly SyncService _syncService;
    private readonly HistoryWriter _historyWriter;

    public event EventHandler? RunCompleted;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SyncNowCommand))]
    private bool _isRunning;

    [ObservableProperty] private DateTime? _lastCompletedAt;
    [ObservableProperty] private int _totalPcaItems;
    [ObservableProperty] private int _changedItems;
    [ObservableProperty] private int _pushedToShopify;
    [ObservableProperty] private int _notInSyncMapCount;
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private string? _fatalError;
    [ObservableProperty] private IReadOnlyList<ErrorGroupViewModel> _errorGroups = [];

    public bool HasErrors => ErrorCount > 0 || !string.IsNullOrEmpty(FatalError);

    public SyncStatusViewModel(SyncService syncService, HistoryWriter historyWriter)
    {
        _syncService = syncService;
        _historyWriter = historyWriter;

        _syncService.SyncCompleted += OnSyncCompleted;

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        timer.Tick += (_, _) =>
        {
            if (!_manualRunInProgress)
                IsRunning = _syncService.IsRunning;
        };
        timer.Start();
    }

    private bool _manualRunInProgress;

    private void OnSyncCompleted(object? sender, SyncResult result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ApplyResult(result);
            RunCompleted?.Invoke(this, EventArgs.Empty);
        });
    }

    [RelayCommand(CanExecute = nameof(CanSyncNow))]
    private async Task SyncNowAsync()
    {
        _manualRunInProgress = true;
        IsRunning = true;
        try
        {
            var result = await _syncService.RunAsync();

            if (result.FatalError == "Sync already in progress.")
                return;

            await SyncResultHistoryMapper.WriteAsync(_historyWriter, result);

            ApplyResult(result);
            RunCompleted?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsRunning = false;
            _manualRunInProgress = false;
        }
    }

    private bool CanSyncNow() => !IsRunning;

    private void ApplyResult(SyncResult result)
    {
        LastCompletedAt = result.CompletedAt;
        TotalPcaItems = result.TotalPcaItems;
        ChangedItems = result.ChangedItems;
        PushedToShopify = result.PushedToShopify;
        NotInSyncMapCount = result.NotInSyncMapCount;
        ErrorCount = result.Errors.Count;
        FatalError = result.FatalError;
        ErrorGroups = result.Errors
            .GroupBy(e => e.Category)
            .Select(g => new ErrorGroupViewModel(
                FriendlyCategory(g.Key),
                g.Select(e => $"{e.PcaItemNum}: {e.Detail}").ToList()))
            .ToList();
        OnPropertyChanged(nameof(HasErrors));
    }

    private static string FriendlyCategory(SyncErrorCategory cat) => cat switch
    {
        SyncErrorCategory.NotInSyncMap        => "Item not in sync map (needs re-bootstrap)",
        SyncErrorCategory.NotFoundInShopify   => "Item not found in Shopify inventory",
        SyncErrorCategory.ConflictOverwritten => "Shopify value overwritten by PCA",
        SyncErrorCategory.RetryFailed         => "Sync failed after retry",
        _                                     => cat.ToString(),
    };
}

public sealed class ErrorGroupViewModel
{
    public string CategoryName { get; }
    public IReadOnlyList<string> Items { get; }

    public ErrorGroupViewModel(string categoryName, IReadOnlyList<string> items)
    {
        CategoryName = categoryName;
        Items = items;
    }
}
