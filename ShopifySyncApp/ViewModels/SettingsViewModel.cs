using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShopifySyncApp.Services;
using SyncJob;

namespace ShopifySyncApp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly SyncService _syncService;

    [ObservableProperty] private string _storeUrl = "";
    [ObservableProperty] private string _accessToken = "";
    [ObservableProperty] private string _pcaConnectionString = "";
    [ObservableProperty] private string _shopifySyncConnectionString = "";
    [ObservableProperty] private decimal _pollIntervalMinutes = 5;
    [ObservableProperty] private bool _autoStartOnLaunch;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _storeUrlError;
    [ObservableProperty] private string? _accessTokenError;
    [ObservableProperty] private string? _pollIntervalError;
    [ObservableProperty] private string? _restartNotice;

    public SettingsViewModel(SettingsService settings, SyncService syncService)
    {
        _settings = settings;
        _syncService = syncService;
        LoadFromFile();
    }

    private void LoadFromFile()
    {
        var root = _settings.Load();
        if (root is null) return;

        StoreUrl = root["Shopify"]?["StoreUrl"]?.GetValue<string>() ?? "";
        AccessToken = root["Shopify"]?["AccessToken"]?.GetValue<string>() ?? "";
        PcaConnectionString = root["ConnectionStrings"]?["PcAmerica"]?.GetValue<string>() ?? "";
        ShopifySyncConnectionString = root["ConnectionStrings"]?["ShopifySync"]?.GetValue<string>() ?? "";
        PollIntervalMinutes = root["App"]?["PollIntervalMinutes"]?.GetValue<int>() ?? 5;
        AutoStartOnLaunch = root["App"]?["AutoStartOnLaunch"]?.GetValue<bool>() ?? false;
    }

    private bool Validate()
    {
        StoreUrlError = string.IsNullOrWhiteSpace(StoreUrl) ? "Store URL is required." : null;
        AccessTokenError = string.IsNullOrWhiteSpace(AccessToken) ? "Access token is required." : null;
        PollIntervalError = PollIntervalMinutes < 1 ? "Poll interval must be at least 1 minute." : null;
        return StoreUrlError is null && AccessTokenError is null && PollIntervalError is null;
    }

    private int PollIntervalInt => (int)Math.Truncate(PollIntervalMinutes);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!Validate()) return;

        IsSaving = true;
        RestartNotice = null;

        try
        {
            var root = _settings.Load();
            var prevPcaConn = root?["ConnectionStrings"]?["PcAmerica"]?.GetValue<string>() ?? "";
            var prevSyncConn = root?["ConnectionStrings"]?["ShopifySync"]?.GetValue<string>() ?? "";
            var prevToken = root?["Shopify"]?["AccessToken"]?.GetValue<string>() ?? "";
            bool connectionChanged = PcaConnectionString != prevPcaConn
                || ShopifySyncConnectionString != prevSyncConn
                || AccessToken != prevToken;

            _settings.SetAll(new Dictionary<string, JsonNode?>
            {
                ["Shopify:StoreUrl"] = JsonValue.Create(StoreUrl),
                ["Shopify:AccessToken"] = JsonValue.Create(AccessToken),
                ["ConnectionStrings:PcAmerica"] = JsonValue.Create(PcaConnectionString),
                ["ConnectionStrings:ShopifySync"] = JsonValue.Create(ShopifySyncConnectionString),
                ["App:PollIntervalMinutes"] = JsonValue.Create(PollIntervalInt),
                ["App:AutoStartOnLaunch"] = JsonValue.Create(AutoStartOnLaunch),
            });

            await Task.Run(() =>
            {
                _syncService.StopScheduler();
                if (AutoStartOnLaunch)
                    _syncService.StartScheduler(TimeSpan.FromMinutes(PollIntervalInt));
            });

            if (connectionChanged)
                RestartNotice = "Restart required for connection changes to take effect.";
        }
        finally
        {
            IsSaving = false;
        }
    }
}
