using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SyncHistory;

namespace ShopifySyncApp.ViewModels;

public partial class HistoryViewModel : ViewModelBase
{
    private readonly HistoryReader _reader;

    [ObservableProperty] private IReadOnlyList<string> _runFileNames = [];
    [ObservableProperty] private string? _selectedFileName;
    [ObservableProperty] private SyncHistoryRecord? _selectedRecord;
    [ObservableProperty] private IReadOnlyList<ErrorGroupViewModel> _selectedRecordErrors = [];

    public HistoryViewModel(HistoryReader reader)
    {
        _reader = reader;
    }

    public void Refresh()
    {
        RunFileNames = _reader.ListRuns();
        if (SelectedFileName is not null && !RunFileNames.Contains(SelectedFileName))
        {
            SelectedFileName = null;
            SelectedRecord = null;
            SelectedRecordErrors = [];
        }
    }

    partial void OnSelectedFileNameChanged(string? value)
    {
        if (value is null)
        {
            SelectedRecord = null;
            SelectedRecordErrors = [];
            return;
        }
        _ = LoadSelectedAsync(value);
    }

    private async Task LoadSelectedAsync(string fileName)
    {
        try
        {
            var record = await _reader.LoadRunAsync(fileName);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SelectedRecord = record;
                SelectedRecordErrors = SelectedRecord is null ? [] :
                    SelectedRecord.Errors
                        .GroupBy(e => e.Category)
                        .Select(g => new ErrorGroupViewModel(
                            FriendlyCategory(g.Key),
                            g.Select(e => $"{e.PcaItemNum}: {e.Detail}").ToList()))
                        .ToList();
            });
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                SelectedRecord = null;
                SelectedRecordErrors = [];
            });
        }
    }

    private static string FriendlyCategory(string category) => category switch
    {
        "NotInSyncMap"        => "Item not in sync map (needs re-bootstrap)",
        "NotFoundInShopify"   => "Item not found in Shopify inventory",
        "ConflictOverwritten" => "Shopify value overwritten by PCA",
        "RetryFailed"         => "Sync failed after retry",
        _                     => category,
    };
}
