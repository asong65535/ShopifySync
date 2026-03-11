namespace SyncHistory;

public static class HistoryPaths
{
    public static string LogsDirectory()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShopifySync",
            "logs");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string FileNameFor(DateTime completedAtUtc) =>
        $"{completedAtUtc:yyyy-MM-dd_HH-mm-ss}_UTC.json";
}
