using System.Net;
using System.Text.Json.Serialization;

namespace CsServerWatch.Core;

public sealed record ParsedAddress(string Host, int Port, string Display, bool PasswordStripped = false);

public sealed class Player
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["index"] = Index,
        ["name"] = Name,
        ["score"] = Score,
        ["duration"] = Math.Round(Duration, 1, MidpointRounding.ToEven),
    };
}

public sealed class ServerInfo
{
    [JsonPropertyName("protocol")]
    public int Protocol { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("map")]
    public string Map { get; set; } = "";

    [JsonPropertyName("folder")]
    public string Folder { get; set; } = "";

    [JsonPropertyName("game")]
    public string Game { get; set; } = "";

    [JsonPropertyName("app_id")]
    public int AppId { get; set; }

    [JsonPropertyName("players")]
    public int Players { get; set; }

    [JsonPropertyName("max_players")]
    public int MaxPlayers { get; set; }

    [JsonPropertyName("bots")]
    public int Bots { get; set; }

    [JsonPropertyName("server_type")]
    public string ServerType { get; set; } = "";

    [JsonPropertyName("environment")]
    public string Environment { get; set; } = "";

    [JsonPropertyName("password")]
    public bool Password { get; set; }

    [JsonPropertyName("vac")]
    public bool Vac { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("keywords")]
    public string Keywords { get; set; } = "";

    [JsonPropertyName("game_id")]
    public ulong? GameId { get; set; }

    [JsonPropertyName("steam_id")]
    public ulong? SteamId { get; set; }

    [JsonPropertyName("query_port")]
    public int? QueryPort { get; set; }

    [JsonPropertyName("tv_port")]
    public int? TvPort { get; set; }

    [JsonPropertyName("tv_name")]
    public string? TvName { get; set; }

    [JsonPropertyName("product")]
    public string? Product { get; set; }
}

public sealed class QueryResult
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required int PingMs { get; init; }
    public required ServerInfo Info { get; init; }
    public List<Player>? Players { get; init; }
    public Dictionary<string, string>? Rules { get; init; }
    public string? PlayersNote { get; init; }
    public string? RulesNote { get; init; }
}

public static class ServerLabels
{
    public static string ServerType(string? value) => value switch
    {
        "d" => "Dedicated",
        "l" => "Listen",
        "p" => "SourceTV",
        _ => string.IsNullOrWhiteSpace(value) ? "Unknown" : value,
    };

    public static string Environment(string? value) => value switch
    {
        "l" => "Linux",
        "w" => "Windows",
        "m" => "macOS",
        "o" => "macOS",
        _ => string.IsNullOrWhiteSpace(value) ? "Unknown" : value,
    };

    public static string Duration(double seconds)
    {
        var total = Math.Max(0, (int)seconds);
        var hours = total / 3600;
        var rem = total % 3600;
        var minutes = rem / 60;
        var secs = rem % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }

    public static string Checked(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "not checked yet";
        }

        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToString("HH:mm:ss")
            : value;
    }
}

public static class Addresses
{
    public static string CanonicalHost(string host)
    {
        var text = host.Trim().TrimEnd('.');
        if (IPAddress.TryParse(text, out var address))
        {
            return address.ToString();
        }

        return text;
    }

    public static string FormatEndpoint(string host, int port)
    {
        if (host.Contains(':') && !host.StartsWith('['))
        {
            return $"[{host}]:{port}";
        }

        return $"{host}:{port}";
    }

    public static string ServerId(string host, int port)
    {
        var key = CanonicalHost(host);
        if (!IPAddress.TryParse(key, out _))
        {
            key = key.ToLowerInvariant();
        }

        return $"{key}|{port}";
    }

    public static ParsedAddress Parse(string text)
    {
        var raw = text.Trim().Trim('"').Trim('\'');
        if (string.IsNullOrWhiteSpace(raw) || raw.Equals("ip:port", StringComparison.OrdinalIgnoreCase) || raw.Equals("host:port", StringComparison.OrdinalIgnoreCase))
        {
            throw new AddressError("Enter a server address first. Nothing was saved.");
        }

        var passwordStripped = false;
        var lowered = raw.ToLowerInvariant();
        if (lowered.StartsWith("steam://", StringComparison.Ordinal))
        {
            var rest = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..];
            if (rest.StartsWith("connect/", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest["connect/".Length..];
            }

            raw = rest;
        }
        else if (lowered.StartsWith("udp://", StringComparison.Ordinal))
        {
            raw = raw[6..];
        }
        else if (lowered.StartsWith("connect ", StringComparison.Ordinal) || lowered.StartsWith("connect\t", StringComparison.Ordinal))
        {
            var pieces = raw.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            raw = pieces.Length == 2 ? pieces[1].Trim() : "";
        }

        raw = raw.Trim();
        if (raw.Contains('/'))
        {
            raw = raw.Split('/', 2)[0];
            passwordStripped = true;
        }

        if (raw.Contains('?'))
        {
            raw = raw.Split('?', 2)[0];
            passwordStripped = true;
        }

        raw = raw.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new AddressError("Enter a server address first. Nothing was saved.");
        }

        var (host, port) = SplitHostPort(raw);
        host = CanonicalHost(host);
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || host.Length > 253)
        {
            throw new AddressError("That address does not look like host:port. Nothing was saved.");
        }

        return new ParsedAddress(host, port, FormatEndpoint(host, port), passwordStripped);
    }

    private static (string Host, int Port) SplitHostPort(string raw)
    {
        if (raw.StartsWith('['))
        {
            var end = raw.IndexOf(']');
            if (end <= 1)
            {
                throw new AddressError("IPv6 addresses need brackets, like [2001:db8::1]:27015. Nothing was saved.");
            }

            var host = raw[1..end].Trim();
            var tail = raw[(end + 1)..].Trim();
            if (string.IsNullOrEmpty(tail))
            {
                return (host, 27015);
            }

            if (!tail.StartsWith(':'))
            {
                throw new AddressError("IPv6 addresses need brackets, like [2001:db8::1]:27015. Nothing was saved.");
            }

            return (host, ParsePort(tail[1..]));
        }

        if (raw.Count(ch => ch == ':') > 1)
        {
            throw new AddressError("IPv6 addresses need brackets, like [2001:db8::1]:27015. Nothing was saved.");
        }

        if (raw.Contains(':'))
        {
            var split = raw.LastIndexOf(':');
            return (raw[..split].Trim(), ParsePort(raw[(split + 1)..]));
        }

        var parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            return (parts[0], 27015);
        }

        if (parts.Length == 2)
        {
            return (parts[0], ParsePort(parts[1]));
        }

        throw new AddressError("That address does not look like host:port. Nothing was saved.");
    }

    private static int ParsePort(string text)
    {
        if (!int.TryParse(text.Trim(), out var port))
        {
            throw new AddressError("That port is not valid. Use 1–65535. Nothing was saved.");
        }

        if (port < 1 || port > 65535)
        {
            throw new AddressError("That port is not valid. Use 1–65535. Nothing was saved.");
        }

        return port;
    }
}
