using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShopifySyncApp.Services;

public sealed class SettingsService
{
    private readonly string _path;
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public SettingsService(string appBasePath)
    {
        _path = Path.Combine(appBasePath, "appsettings.local.json");
    }

    public JsonNode? Load()
    {
        if (!File.Exists(_path)) return null;
        return JsonNode.Parse(File.ReadAllText(_path));
    }

    public void Set(string keyPath, JsonNode? value)
    {
        var root = Load() as JsonObject ?? new JsonObject();
        SetNode(root, keyPath, value);
        File.WriteAllText(_path, root.ToJsonString(WriteOptions));
    }

    public void SetMany(IReadOnlyDictionary<string, string?> values)
    {
        var root = Load() as JsonObject ?? new JsonObject();
        foreach (var (keyPath, value) in values)
            SetNode(root, keyPath, value is null ? null : JsonValue.Create(value));
        File.WriteAllText(_path, root.ToJsonString(WriteOptions));
    }

    public void SetAll(IReadOnlyDictionary<string, JsonNode?> values)
    {
        var root = Load() as JsonObject ?? new JsonObject();
        foreach (var (keyPath, value) in values)
            SetNode(root, keyPath, value);
        File.WriteAllText(_path, root.ToJsonString(WriteOptions));
    }

    private static void SetNode(JsonObject root, string keyPath, JsonNode? value)
    {
        var parts = keyPath.Split(':');
        JsonObject node = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (node[parts[i]] is not JsonObject child)
            {
                child = new JsonObject();
                node[parts[i]] = child;
            }
            node = child;
        }
        node[parts[^1]] = value;
    }
}
