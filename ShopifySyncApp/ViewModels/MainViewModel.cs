using CommunityToolkit.Mvvm.ComponentModel;

namespace ShopifySyncApp.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public SyncStatusViewModel SyncStatus { get; }
    public HistoryViewModel History { get; }

    [ObservableProperty] private int _selectedTabIndex;

    public MainViewModel(SyncStatusViewModel syncStatus, HistoryViewModel history)
    {
        SyncStatus = syncStatus;
        History = history;

        SyncStatus.RunCompleted += (_, _) => History.Refresh();
    }

    public void OnHistoryTabActivated() => History.Refresh();
}
