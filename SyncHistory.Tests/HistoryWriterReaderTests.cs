using System.Text.Json;
using Xunit;

namespace SyncHistory.Tests;

public sealed class HistoryWriterReaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public HistoryWriterReaderTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private HistoryWriter MakeWriter() => new(_tempDir);
    private HistoryReader MakeReader() => new(_tempDir);

    [Fact]
    public async Task WriteAsync_CreatesFileWithCorrectName()
    {
        var writer = MakeWriter();
        var at = new DateTime(2026, 3, 11, 14, 32, 0, DateTimeKind.Utc);

        await writer.WriteAsync(true, at, 10, 2, 2, 0, 0, 0, [], null);

        var files = Directory.GetFiles(_tempDir, "*_UTC.json");
        Assert.Single(files);
        Assert.Contains("2026-03-11_14-32-00_UTC.json", files[0]);
    }

    [Fact]
    public async Task RoundTrip_AllFieldsPreserved()
    {
        var writer = MakeWriter();
        var reader = MakeReader();
        var at = new DateTime(2026, 3, 11, 14, 32, 0, DateTimeKind.Utc);
        var errors = new[] { ("ITEM001", "RetryFailed", (string?)"some detail") };

        await writer.WriteAsync(false, at, 100, 5, 4, 1, 2, 1, errors, "fatal msg");

        var files = reader.ListRuns();
        Assert.Single(files);

        var record = await reader.LoadRunAsync(files[0]);
        Assert.NotNull(record);
        Assert.False(record!.Success);
        Assert.Equal(at, record.CompletedAt);
        Assert.Equal(DateTimeKind.Utc, record.CompletedAt.Kind);
        Assert.Equal(100, record.TotalPcaItems);
        Assert.Equal(5, record.ChangedItems);
        Assert.Equal(4, record.PushedToShopify);
        Assert.Equal(1, record.PulledFromShopify);
        Assert.Equal(2, record.ConflictsPcaWon);
        Assert.Equal(1, record.NotInSyncMapCount);
        Assert.Equal("fatal msg", record.FatalError);
        Assert.Single(record.Errors);
        Assert.Equal("ITEM001", record.Errors[0].PcaItemNum);
        Assert.Equal("RetryFailed", record.Errors[0].Category);
        Assert.Equal("some detail", record.Errors[0].Detail);
    }

    [Fact]
    public async Task LoadRunAsync_ReturnNullForMissingFile()
    {
        var reader = MakeReader();
        var result = await reader.LoadRunAsync("nonexistent_UTC.json");
        Assert.Null(result);
    }

    [Fact]
    public async Task ListRuns_ReturnsNewestFirst()
    {
        var writer = MakeWriter();
        var t1 = new DateTime(2026, 3, 11, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 3, 11, 14, 0, 0, DateTimeKind.Utc);

        await writer.WriteAsync(true, t1, 0, 0, 0, 0, 0, 0, [], null);
        await writer.WriteAsync(true, t2, 0, 0, 0, 0, 0, 0, [], null);

        var files = MakeReader().ListRuns();
        Assert.Equal(2, files.Count);
        Assert.True(string.Compare(files[0], files[1], StringComparison.Ordinal) > 0,
            "First entry should be lexicographically later (newer) than second.");
    }
}
