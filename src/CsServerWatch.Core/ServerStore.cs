using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CsServerWatch.Core;

public sealed class ServerStore
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ServerStore(string path)
    {
        Path = path;
        Load();
    }

    public string Path { get; }
    public List<JsonObject> Servers { get; private set; } = [];
    public string? LoadError { get; private set; }

    public void Load()
    {
        LoadError = null;
        if (!File.Exists(Path))
        {
            Servers = [];
            return;
        }

        try
        {
            var data = JsonNode.Parse(File.ReadAllText(Path, Utf8));
            var raw = data is JsonObject obj ? obj["servers"] as JsonArray : null;
            var servers = new List<JsonObject>();
            if (raw is not null)
            {
                foreach (var item in raw)
                {
                    if (item is JsonObject record && record["host"] is not null && record["port"] is not null)
                    {
                        servers.Add(record);
                    }
                }
            }

            Servers = servers;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Servers = [];
            LoadError = $"Could not read saved servers ({ex.Message}).";
        }
    }

    public void Save()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var root = new JsonObject
        {
            ["servers"] = new JsonArray(Servers.Select(server => server.DeepClone()).ToArray()),
        };
        var text = root.ToJsonString(JsonOptions) + "\n";
        var temporary = System.IO.Path.ChangeExtension(Path, ".json.tmp");
        File.WriteAllText(temporary, text, Utf8);
        File.Move(temporary, Path, overwrite: true);
    }

    public JsonObject? Get(string serverId)
    {
        return Servers.FirstOrDefault(record => JsonText(record["id"]) == serverId);
    }

    public JsonObject Upsert(JsonObject record)
    {
        var id = JsonText(record["id"]) ?? "";
        var existing = Get(id);
        if (existing is null)
        {
            record["generation"] = 1;
            Servers.Insert(0, record);
            Save();
            return record;
        }

        var added = existing["added_at"];
        record["added_at"] = added is null ? record["added_at"]?.DeepClone() : added.DeepClone();
        record["generation"] = JsonInt(existing["generation"]) + 1;
        if (IsNull(record["players"]))
        {
            record["players"] = existing["players"]?.DeepClone() ?? NullNode();
            record["players_note"] = existing["players_note"]?.DeepClone() ?? NullNode();
        }

        if (IsNull(record["rules"]))
        {
            record["rules"] = existing["rules"]?.DeepClone() ?? NullNode();
            record["rules_note"] = existing["rules_note"]?.DeepClone() ?? NullNode();
        }

        var index = Servers.FindIndex(current => JsonText(current["id"]) == id);
        if (index >= 0)
        {
            Servers[index] = record;
        }

        Save();
        return record;
    }

    public JsonObject? Update(string serverId, IReadOnlyDictionary<string, JsonNode?> changes)
    {
        var record = Get(serverId);
        if (record is null)
        {
            return null;
        }

        foreach (var (key, value) in changes)
        {
            record[key] = value?.DeepClone() ?? NullNode();
        }

        record["generation"] = JsonInt(record["generation"]) + 1;
        Save();
        return record;
    }

    public bool Remove(string serverId)
    {
        var before = Servers.Count;
        Servers = Servers.Where(item => JsonText(item["id"]) != serverId).ToList();
        if (Servers.Count == before)
        {
            return false;
        }

        Save();
        return true;
    }

    public static string ResolvePath(string? exePath = null)
    {
        var exe = exePath ?? Environment.ProcessPath;
        var start = string.IsNullOrWhiteSpace(exe)
            ? AppContext.BaseDirectory
            : System.IO.Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "servers.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return System.IO.Path.Combine(start, "servers.json");
    }

    public static JsonObject FromResult(QueryResult result, string? passwordNote)
    {
        var now = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
        var info = JsonSerializer.SerializeToNode(result.Info, JsonOptions)!.AsObject();
        return new JsonObject
        {
            ["id"] = Addresses.ServerId(result.Host, result.Port),
            ["host"] = result.Host,
            ["port"] = result.Port,
            ["display"] = Addresses.FormatEndpoint(result.Host, result.Port),
            ["added_at"] = now,
            ["last_checked"] = now,
            ["online"] = true,
            ["error"] = NullNode(),
            ["ping_ms"] = result.PingMs,
            ["info"] = info,
            ["players"] = PlayersNode(result.Players),
            ["players_note"] = result.PlayersNote is null ? NullNode() : result.PlayersNote,
            ["rules"] = result.Rules is null ? NullNode() : JsonSerializer.SerializeToNode(result.Rules, JsonOptions),
            ["rules_note"] = result.RulesNote is null ? NullNode() : result.RulesNote,
            ["password_note"] = passwordNote is null ? NullNode() : passwordNote,
        };
    }

    public static JsonNode PlayersNode(List<Player>? players)
    {
        if (players is null)
        {
            return NullNode();
        }

        var array = new JsonArray();
        foreach (var player in players)
        {
            array.Add(JsonSerializer.SerializeToNode(new
            {
                index = player.Index,
                name = player.Name,
                score = player.Score,
                duration = Math.Round(player.Duration, 1, MidpointRounding.ToEven),
            }));
        }

        return array;
    }

    public static JsonNode NullNode() => JsonValue.Create((string?)null) ?? JsonNode.Parse("null")!;

    public static bool IsNull(JsonNode? node) => node is null || node.GetValueKind() == JsonValueKind.Null;

    public static string? JsonText(JsonNode? node)
    {
        if (IsNull(node))
        {
            return null;
        }

        return node!.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    }

    public static int JsonInt(JsonNode? node, int fallback = 0)
    {
        if (IsNull(node))
        {
            return fallback;
        }

        return node!.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<int>(),
            JsonValueKind.String when int.TryParse(node.GetValue<string>(), out var parsed) => parsed,
            _ => fallback,
        };
    }
}
