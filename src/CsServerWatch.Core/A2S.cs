using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ICSharpCode.SharpZipLib.BZip2;

namespace CsServerWatch.Core;

public static class A2S
{
    public const byte Info = 0x54;
    public const byte Player = 0x55;
    public const byte Rules = 0x56;
    public const byte Challenge = 0x41;
    public const byte InfoResponse = 0x49;
    public const byte PlayerResponse = 0x44;
    public const byte RulesResponse = 0x45;
    public const byte GoldSrc = 0x6D;
    public const int MaxFragments = 32;
    public const int MaxDecompressed = 1024 * 1024;

    private static readonly byte[] InfoMagic = "Source Engine Query\0"u8.ToArray();
    private static readonly string[] SensitiveRuleParts = ["password", "rcon", "token", "secret", "gslt", "steamaccount"];

    public static string? Classify(ServerInfo info)
    {
        var game = (info.Game ?? "").ToLowerInvariant();
        var folder = (info.Folder ?? "").ToLowerInvariant();
        var gameId = info.GameId ?? 0;
        var app730 = info.AppId == 730 || gameId == 730 || (gameId & 0xFFFFFF) == 730;
        var folderCs = folder == "csgo" && game.Contains("counter-strike", StringComparison.Ordinal) && info.AppId is 0 or 730;
        if (!app730 && !folderCs)
        {
            return null;
        }

        if (game.Contains("counter-strike 2", StringComparison.Ordinal))
        {
            return "CS2";
        }

        if (game.Contains("global offensive", StringComparison.Ordinal))
        {
            return "CS:GO";
        }

        return app730 ? "CS2" : "CS:GO";
    }

    public static string RejectionMessage(ServerInfo info)
    {
        var game = string.IsNullOrWhiteSpace(info.Game) ? "unknown game" : info.Game;
        return $"This is not a CS:GO or CS2 server ({game}, app {info.AppId}).";
    }

    public static Dictionary<string, string> RedactRules(Dictionary<string, string> rules)
    {
        var hidden = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in rules)
        {
            var lowered = key.ToLowerInvariant();
            hidden[key] = SensitiveRuleParts.Any(part => lowered.Contains(part, StringComparison.Ordinal))
                ? "(hidden)"
                : value;
        }

        return hidden;
    }

    public static ServerInfo ParseInfo(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);
        var kind = reader.U8();
        if (kind == GoldSrc)
        {
            throw new GoldSrcResponse();
        }

        if (kind != InfoResponse)
        {
            throw new ProtocolError($"unexpected info type {kind:X}");
        }

        var info = new ServerInfo
        {
            Protocol = reader.U8(),
            Name = reader.CString(),
            Map = reader.CString(),
            Folder = reader.CString(),
            Game = reader.CString(),
            AppId = reader.U16(),
            Players = reader.U8(),
            MaxPlayers = reader.U8(),
            Bots = reader.U8(),
            ServerType = ((char)reader.U8()).ToString(),
            Environment = ((char)reader.U8()).ToString(),
            Password = reader.U8() == 1,
            Vac = reader.U8() == 1,
            Version = reader.CString(),
        };
        if (reader.Remaining > 0)
        {
            var edf = reader.U8();
            if ((edf & 0x80) != 0)
            {
                info.QueryPort = reader.U16();
            }

            if ((edf & 0x10) != 0)
            {
                info.SteamId = reader.U64();
            }

            if ((edf & 0x40) != 0)
            {
                info.TvPort = reader.U16();
                info.TvName = reader.CString();
            }

            if ((edf & 0x20) != 0)
            {
                info.Keywords = reader.CString();
            }

            if ((edf & 0x01) != 0)
            {
                info.GameId = reader.U64();
            }
        }

        info.Product = Classify(info);
        return info;
    }

    public static List<Player> ParsePlayers(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);
        if (reader.U8() != PlayerResponse)
        {
            throw new ProtocolError("unexpected player response");
        }

        var count = reader.U8();
        var players = new List<Player>(count);
        for (var i = 0; i < count; i++)
        {
            players.Add(new Player
            {
                Index = reader.U8(),
                Name = reader.CString(),
                Score = reader.I32(),
                Duration = reader.F32(),
            });
        }

        return players;
    }

    public static Dictionary<string, string> ParseRules(ReadOnlySpan<byte> payload)
    {
        var reader = new Reader(payload);
        if (reader.U8() != RulesResponse)
        {
            throw new ProtocolError("unexpected rules response");
        }

        var count = reader.U16();
        if (count > 2048)
        {
            throw new ProtocolError("too many rules");
        }

        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = reader.CString();
            rules[key] = reader.CString();
        }

        return RedactRules(rules);
    }

    public static QueryResult Query(
        string host,
        int port,
        double timeoutSeconds = 4,
        bool includePlayers = false,
        bool includeRules = false)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        NoResponse? last = null;
        foreach (var endpoint in Candidates(host, port))
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining < TimeSpan.FromSeconds(0.3))
            {
                break;
            }

            try
            {
                return QueryEndpoint(endpoint, host, port, remaining, includePlayers, includeRules);
            }
            catch (NoResponse ex)
            {
                last = ex;
            }
        }

        throw last ?? new NoResponse();
    }

    private static List<IPEndPoint> Candidates(string host, int port)
    {
        IPAddress[] addresses;
        try
        {
            addresses = Dns.GetHostAddresses(host);
        }
        catch (SocketException ex)
        {
            throw new ResolveError("Could not resolve that address.");
        }

        var found = new List<IPEndPoint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in addresses)
        {
            var key = address.ToString();
            if (!seen.Add(key))
            {
                continue;
            }

            found.Add(new IPEndPoint(address, port));
        }

        if (found.Count == 0)
        {
            throw new ResolveError("Could not resolve that address.");
        }

        return found;
    }

    private static QueryResult QueryEndpoint(
        IPEndPoint endpoint,
        string host,
        int port,
        TimeSpan timeout,
        bool includePlayers,
        bool includeRules)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Connect(endpoint);
            var started = DateTime.UtcNow;
            var payload = Exchange(socket, "info", timeout);
            var ping = Math.Max(0, (int)Math.Round((DateTime.UtcNow - started).TotalMilliseconds));
            ServerInfo info;
            try
            {
                info = ParseInfo(payload);
            }
            catch (GoldSrcResponse ex)
            {
                throw new NotCsServer("This answered as a GoldSrc server (CS 1.6 or similar), not CS:GO or CS2.");
            }
            catch (ProtocolError)
            {
                throw new NotAServerQuery();
            }

            if (string.IsNullOrEmpty(info.Product))
            {
                throw new NotCsServer(RejectionMessage(info));
            }

            List<Player>? players = null;
            string? playersNote = null;
            Dictionary<string, string>? rules = null;
            string? rulesNote = null;
            if (includePlayers)
            {
                (players, playersNote) = OptionalPlayers(socket, info);
            }

            if (includeRules)
            {
                (rules, rulesNote) = OptionalRules(socket);
            }

            return new QueryResult
            {
                Host = Addresses.CanonicalHost(host),
                Port = port,
                PingMs = ping,
                Info = info,
                Players = players,
                Rules = rules,
                PlayersNote = playersNote,
                RulesNote = rulesNote,
            };
        }
        finally
        {
            socket.Close();
        }
    }

    private static (List<Player>? Players, string? Note) OptionalPlayers(Socket socket, ServerInfo info)
    {
        List<Player> players;
        try
        {
            players = ParsePlayers(Exchange(socket, "players", TimeSpan.FromSeconds(2)));
        }
        catch (Exception ex) when (ex is QueryError or ProtocolError)
        {
            return (null, "Player names could not be loaded. CS2 often hides them.");
        }

        var named = players.Where(player => !string.IsNullOrWhiteSpace(player.Name)).ToList();
        if (players.Count > 0 && named.Count == 0)
        {
            return (players, "Player names are hidden by this server. That is common on CS2.");
        }

        if (info.Players > 0 && players.Count == 0)
        {
            return (players, "The server did not share player names. CS2 often hides them.");
        }

        return (players, null);
    }

    private static (Dictionary<string, string>? Rules, string? Note) OptionalRules(Socket socket)
    {
        try
        {
            return (ParseRules(Exchange(socket, "rules", TimeSpan.FromSeconds(2))), null);
        }
        catch (Exception ex) when (ex is QueryError or ProtocolError)
        {
            return (null, "Rules could not be loaded.");
        }
    }

    private static byte[] BuildRequest(string kind, byte[] challenge)
    {
        var body = new List<byte>();
        if (kind == "info")
        {
            body.Add(Info);
            body.AddRange(InfoMagic);
            body.AddRange(challenge);
        }
        else if (kind == "players")
        {
            body.Add(Player);
            body.AddRange(challenge);
        }
        else if (kind == "rules")
        {
            body.Add(Rules);
            body.AddRange(challenge);
        }
        else
        {
            throw new ArgumentException(kind);
        }

        var packet = new byte[4 + body.Count];
        BinaryPrimitives.WriteInt32LittleEndian(packet, -1);
        body.CopyTo(packet, 4);
        return packet;
    }

    private static bool IsReset(SocketException exception) =>
        exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.TimedOut
        || exception.ErrorCode is 10054 or 10053 or 10060;

    private static byte[] Exchange(Socket socket, string kind, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var challenge = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
        var sends = 0;
        while (sends < 4)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new NoResponse();
            }

            try
            {
                socket.Send(BuildRequest(kind, challenge));
            }
            catch (SocketException ex) when (IsReset(ex))
            {
                throw new NoResponse();
            }

            sends++;
            byte[] payload;
            try
            {
                payload = ReadResponse(socket, deadline);
            }
            catch (NoResponse)
            {
                if (sends >= 2)
                {
                    throw;
                }

                continue;
            }

            if (payload.Length >= 5 && payload[0] == Challenge)
            {
                challenge = payload[1..5];
                continue;
            }

            if (payload.Length == 0)
            {
                continue;
            }

            return payload;
        }

        throw new NoResponse();
    }

    private static byte[] ReadResponse(Socket socket, DateTime deadline)
    {
        var fragments = new Dictionary<int, byte[]>();
        int? expected = null;
        int? ident = null;
        var compressed = false;
        var buffer = new byte[65535];
        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new NoResponse();
            }

            socket.ReceiveTimeout = Math.Max(1, (int)remaining.TotalMilliseconds);
            int read;
            try
            {
                read = socket.Receive(buffer);
            }
            catch (SocketException ex) when (IsReset(ex) || ex.SocketErrorCode == SocketError.TimedOut)
            {
                throw new NoResponse();
            }

            if (read < 4)
            {
                continue;
            }

            var header = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(0, 4));
            if (header == -1)
            {
                if (fragments.Count > 0)
                {
                    continue;
                }

                return Normalize(buffer.AsSpan(4, read - 4).ToArray());
            }

            if (header != -2)
            {
                continue;
            }

            if (!TryParseFragment(buffer.AsSpan(0, read), out var fragId, out var total, out var number, out var fragCompressed, out var payload))
            {
                continue;
            }

            if (ident is null)
            {
                ident = fragId;
                expected = total;
                compressed = fragCompressed;
            }
            else if (fragId != ident || total != expected)
            {
                continue;
            }

            fragments[number] = payload;
            if (expected is not null && fragments.Count == expected)
            {
                return Assemble(fragments, expected.Value, compressed);
            }
        }
    }

    private static bool TryParseFragment(
        ReadOnlySpan<byte> data,
        out int ident,
        out int total,
        out int number,
        out bool compressed,
        out byte[] payload)
    {
        ident = 0;
        total = 0;
        number = 0;
        compressed = false;
        payload = [];
        if (data.Length < 12)
        {
            return false;
        }

        var header = BinaryPrimitives.ReadInt32LittleEndian(data);
        var rawIdent = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        total = data[8];
        number = data[9];
        if (header != -2 || total is < 1 or > MaxFragments || number >= total)
        {
            return false;
        }

        ident = (int)(rawIdent & 0x7FFFFFFF);
        compressed = (rawIdent & 0x80000000) != 0;
        payload = data[12..].ToArray();
        return true;
    }

    private static byte[] Assemble(Dictionary<int, byte[]> fragments, int total, bool compressed)
    {
        var payload = fragments.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value).ToArray();
        if (fragments.Count != total || Enumerable.Range(0, total).Any(index => !fragments.ContainsKey(index)))
        {
            throw new ProtocolError("missing split packet");
        }

        payload = new byte[Enumerable.Range(0, total).Sum(index => fragments[index].Length)];
        var offset = 0;
        for (var index = 0; index < total; index++)
        {
            var chunk = fragments[index];
            chunk.CopyTo(payload, offset);
            offset += chunk.Length;
        }

        if (!compressed)
        {
            return Normalize(payload);
        }

        if (payload.Length < 8)
        {
            throw new ProtocolError("short compressed payload");
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(payload);
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
        if (size is < 0 || size > MaxDecompressed)
        {
            throw new ProtocolError("compressed payload too large");
        }

        byte[] decoded;
        try
        {
            using var input = new MemoryStream(payload, 8, payload.Length - 8, writable: false);
            using var bz = new BZip2InputStream(input);
            using var output = new MemoryStream();
            bz.CopyTo(output);
            decoded = output.ToArray();
        }
        catch (Exception ex)
        {
            throw new ProtocolError("bzip2 decompress failed");
        }

        if (decoded.Length != size)
        {
            throw new ProtocolError("decompressed size mismatch");
        }

        if (Crc32(decoded) != checksum)
        {
            throw new ProtocolError("decompressed checksum mismatch");
        }

        return Normalize(decoded);
    }

    private static byte[] Normalize(byte[] payload)
    {
        if (payload.Length > 4 && payload[0] == 0xFF && payload[1] == 0xFF && payload[2] == 0xFF && payload[3] == 0xFF)
        {
            return payload[4..];
        }

        return payload;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    private sealed class Reader
    {
        private readonly byte[] _data;
        private int _offset;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data.ToArray();
        }

        public int Remaining => _data.Length - _offset;

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public string CString()
        {
            var end = Array.IndexOf(_data, (byte)0, _offset);
            if (end < 0)
            {
                throw new ProtocolError("unterminated string");
            }

            var text = Encoding.UTF8.GetString(_data, _offset, end - _offset);
            _offset = end + 1;
            return text;
        }

        private byte[] Take(int size)
        {
            if (Remaining < size)
            {
                throw new ProtocolError("response ended early");
            }

            var slice = _data.AsSpan(_offset, size).ToArray();
            _offset += size;
            return slice;
        }
    }
}
