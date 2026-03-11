using System.Text.Json;

namespace SyncHistory;

public sealed class HistoryReader
{
    private readonly string? _directoryOverride;

    private static readonly JsonSerializerOptions Options = new();

    public HistoryReader(string? directoryOverride = null)
    {
        _directoryOverride = directoryOverride;
    }

    public IReadOnlyList<string> ListRuns()
    {
        var dir = _directoryOverride ?? HistoryPaths.LogsDirectory();
        return Directory.GetFiles(dir, "*_UTC.json")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderByDescending(f => f)
            .ToList();
    }

    public async Task<SyncHistoryRecord?> LoadRunAsync(string fileName)
    {
        var dir = _directoryOverride ?? HistoryPaths.LogsDirectory();
        var path = Path.Combine(dir, fileName);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SyncHistoryRecord>(stream, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
