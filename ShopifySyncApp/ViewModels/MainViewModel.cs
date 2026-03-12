using CommunityToolkit.Mvvm.ComponentModel;

namespace ShopifySyncApp.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public SyncStatusViewModel SyncStatus { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private int _selectedTabIndex;

    public MainViewModel(SyncStatusViewModel syncStatus, HistoryViewModel history, SettingsViewModel settings)
    {
        SyncStatus = syncStatus;
        History = history;
        Settings = settings;

        SyncStatus.RunCompleted += (_, _) => History.Refresh();
    }

    public void OnHistoryTabActivated() => History.Refresh();
}
