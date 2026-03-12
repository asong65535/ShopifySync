using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SyncHistory;

namespace ShopifySyncApp.ViewModels;

public partial class HistoryViewModel : ViewModelBase
{
    private readonly HistoryReader _reader;

    [ObservableProperty] private IReadOnlyList<string> _runFileNames = [];
    [ObservableProperty] private string? _selectedDisplayName;
    [ObservableProperty] private SyncHistoryRecord? _selectedRecord;
    [ObservableProperty] private IReadOnlyList<ErrorGroupViewModel> _selectedRecordErrors = [];

    public IReadOnlyList<string> DisplayFileNames =>
        RunFileNames.Select(f => Path.GetFileNameWithoutExtension(f)).ToList();

    public HistoryViewModel(HistoryReader reader)
    {
        _reader = reader;
    }

    public void Refresh()
    {
        RunFileNames = _reader.ListRuns();
        OnPropertyChanged(nameof(DisplayFileNames));
        if (SelectedDisplayName is not null &&
            !DisplayFileNames.Contains(SelectedDisplayName))
        {
            SelectedDisplayName = null;
            SelectedRecord = null;
            SelectedRecordErrors = [];
        }
    }

    partial void OnSelectedDisplayNameChanged(string? value)
    {
        if (value is null)
        {
            SelectedRecord = null;
            SelectedRecordErrors = [];
            return;
        }
        var real = RunFileNames.FirstOrDefault(f =>
            Path.GetFileNameWithoutExtension(f) == value);
        if (real is not null)
            _ = LoadSelectedAsync(real);
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
