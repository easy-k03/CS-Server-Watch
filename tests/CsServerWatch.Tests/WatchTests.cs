using Xunit;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using CsServerWatch.Core;

namespace CsServerWatch.Tests;

public class AddressTests
{
    [Fact]
    public void CommonForms()
    {
        Assert.Equal("192.0.2.10:27016", Addresses.Parse("192.0.2.10:27016").Display);
        Assert.Equal(27015, Addresses.Parse("192.0.2.10").Port);
        Assert.Equal("192.0.2.10:27015", Addresses.Parse("connect 192.0.2.10:27015").Display);
        Assert.Equal(27015, Addresses.Parse("connect 192.0.2.10 27015").Port);
        var parsed = Addresses.Parse("steam://connect/192.0.2.10:27015/secret");
        Assert.True(parsed.PasswordStripped);
        Assert.Equal("192.0.2.10:27015", parsed.Display);
        Assert.Equal("[2001:db8::1]:27015", Addresses.Parse("[2001:db8::1]:27015").Display);
        Assert.Equal(Addresses.ServerId("EXAMPLE.com", 27015), Addresses.ServerId("example.com", 27015));
    }

    [Fact]
    public void RejectsBadInputWithoutClaimingItWasSaved()
    {
        var empty = Assert.Throws<AddressError>(() => Addresses.Parse(""));
        Assert.Contains("nothing was saved", empty.Message, StringComparison.OrdinalIgnoreCase);
        var port = Assert.Throws<AddressError>(() => Addresses.Parse("192.0.2.10:99999"));
        Assert.Contains("nothing was saved", port.Message, StringComparison.OrdinalIgnoreCase);
    }
}

public class ClassifyTests
{
    [Fact]
    public void Cs2AndCsgoAreAccepted()
    {
        Assert.Equal("CS2", A2S.ParseInfo(Payloads.Info()).Product);
        Assert.Equal("CS:GO", A2S.ParseInfo(Payloads.Info(game: "Counter-Strike: Global Offensive", appId: 730)).Product);
        Assert.Equal("CS:GO", A2S.ParseInfo(Payloads.Info(game: "Counter-Strike: Global Offensive", appId: 0, folder: "csgo")).Product);
        Assert.Equal("CS2", A2S.Classify(A2S.ParseInfo(Payloads.Info(game: "Counter-Strike", appId: 730))));
    }

    [Fact]
    public void OtherGamesAreRejected()
    {
        var tf2 = A2S.ParseInfo(Payloads.Info(game: "Team Fortress 2", folder: "tf", appId: 440, mapName: "cp_badlands"));
        Assert.Null(tf2.Product);
        var source = A2S.ParseInfo(Payloads.Info(game: "Counter-Strike: Source", folder: "cstrike", appId: 240, mapName: "de_dust"));
        Assert.Null(source.Product);
        Assert.ThrowsAny<Exception>(() => A2S.ParseInfo(new byte[] { 0x6D }.Concat("this is goldsrc"u8.ToArray()).ToArray()));
    }

    [Fact]
    public void EdfFieldOrder()
    {
        var extra = new List<byte>();
        extra.AddRange(BitConverter.GetBytes((ushort)27016));
        extra.AddRange(BitConverter.GetBytes(76561197960265728UL));
        extra.AddRange(BitConverter.GetBytes((ushort)27020));
        extra.AddRange(Payloads.CString("TV"));
        extra.AddRange(Payloads.CString("competitive,64tick"));
        extra.AddRange(BitConverter.GetBytes(730UL));
        var info = A2S.ParseInfo(Payloads.Info(edf: 0xF1, extra: extra.ToArray()));
        Assert.Equal(27016, info.QueryPort);
        Assert.Equal(76561197960265728UL, info.SteamId);
        Assert.Equal(27020, info.TvPort);
        Assert.Equal("TV", info.TvName);
        Assert.Equal("competitive,64tick", info.Keywords);
        Assert.Equal(730UL, info.GameId);
        Assert.Equal(4, info.Players);
        Assert.Equal(10, info.MaxPlayers);
    }
}

public class DetailTests
{
    [Fact]
    public void PlayersAndRedactedRules()
    {
        var players = A2S.ParsePlayers(Payloads.Players([(0, "alice", 12, 65.5f)]));
        Assert.Equal("alice", players[0].Name);
        Assert.Equal(12, players[0].Score);
        Assert.Equal(65.5, players[0].Duration, 2);
        var rules = A2S.ParseRules(Payloads.Rules(
        [
            ("mp_maxrounds", "24"),
            ("sv_password", "hunter2"),
            ("rcon_password", "nope"),
            ("sv_setsteamaccount", "token-value"),
        ]));
        Assert.Equal("24", rules["mp_maxrounds"]);
        Assert.Equal("(hidden)", rules["sv_password"]);
        Assert.Equal("(hidden)", rules["rcon_password"]);
        Assert.Equal("(hidden)", rules["sv_setsteamaccount"]);
        Assert.Equal("(hidden)", A2S.RedactRules(new Dictionary<string, string> { ["secret_key"] = "abc", ["hostname"] = "x" })["secret_key"]);
    }
}

public class QueryTests
{
    [Fact]
    public void SavesOnlyAfterCsResponse()
    {
        var responses = new Dictionary<byte, object>
        {
            [0x54] = Payloads.Info(name: "Local CS2", players: 6, maxPlayers: 12, bots: 2),
            [0x55] = Payloads.Players([(0, "mia", 5, 10f)]),
            [0x56] = Payloads.Rules([("sv_password", "hide-me"), ("mp_friendlyfire", "1")]),
        };
        using var server = new FakeServer(Payloads.ChallengeHandler(responses));
        var result = A2S.Query("127.0.0.1", server.Port, 2, includePlayers: true, includeRules: true);
        Assert.Equal("CS2", result.Info.Product);
        Assert.Equal(6, result.Info.Players);
        Assert.Equal("mia", result.Players![0].Name);
        Assert.Equal("(hidden)", result.Rules!["sv_password"]);
        Assert.Equal("1", result.Rules["mp_friendlyfire"]);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, server.Received[0][^4..]);
        Assert.Contains("Source Engine Query", Encoding.ASCII.GetString(server.Received[0]));
    }

    [Fact]
    public void NonCsAnswerIsAHardReject()
    {
        var responses = new Dictionary<byte, object>
        {
            [0x54] = Payloads.Info(name: "Not CS", game: "Team Fortress 2", folder: "tf", appId: 440, mapName: "cp_badlands"),
        };
        using var server = new FakeServer(Payloads.ChallengeHandler(responses));
        var caught = Assert.Throws<NotCsServer>(() => A2S.Query("127.0.0.1", server.Port, 2));
        Assert.Contains("not a CS:GO or CS2 server", caught.Message);
        Assert.Contains("Team Fortress 2", caught.Message);
    }

    [Fact]
    public void NonCsAnswerIsNotWritten()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cswatch-reject-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "servers.json");
            var store = new ServerStore(path);
            var responses = new Dictionary<byte, object>
            {
                [0x54] = Payloads.Info(name: "Not CS", game: "Team Fortress 2", folder: "tf", appId: 440, mapName: "cp_badlands"),
            };
            using var server = new FakeServer(Payloads.ChallengeHandler(responses));
            QueryResult? result = null;
            try
            {
                result = A2S.Query("127.0.0.1", server.Port, 2);
            }
            catch (NotCsServer)
            {
                result = null;
            }

            if (result?.Info.Product is not null)
            {
                store.Upsert(new JsonObject { ["id"] = "should-not", ["host"] = "127.0.0.1", ["port"] = server.Port });
            }

            Assert.False(File.Exists(path));
            Assert.Empty(store.Servers);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void NoAnswerIsNotCalledADifferentGame()
    {
        using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        sock.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)sock.LocalEndPoint!).Port;
        sock.Close();
        var caught = Assert.Throws<NoResponse>(() => A2S.Query("127.0.0.1", port, 1.5));
        Assert.Contains("no cs:go or cs2 server answered", caught.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("team fortress", caught.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitAndCompressedInfo()
    {
        var payload = Payloads.Info(name: "Split CS2 Server");
        foreach (var packets in new[] { Payloads.Split(payload, reverse: true), Payloads.Split(payload, compressed: true) })
        {
            using var server = new FakeServer(Payloads.ChallengeHandler(new Dictionary<byte, object> { [0x54] = packets }));
            var result = A2S.Query("127.0.0.1", server.Port, 2);
            Assert.Equal("Split CS2 Server", result.Info.Name);
            Assert.Equal("CS2", result.Info.Product);
        }
    }

    [Fact]
    public void EndpointFormat()
    {
        Assert.Equal("[2001:db8::1]:27015", Addresses.FormatEndpoint("2001:db8::1", 27015));
    }
}

public class StoreTests
{
    [Fact]
    public void RoundTripAndUpdateWithoutDroppingPlayers()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cswatch-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "servers.json");
            var store = new ServerStore(path);
            store.Upsert(new JsonObject
            {
                ["id"] = "192.0.2.10|27015",
                ["host"] = "192.0.2.10",
                ["port"] = 27015,
                ["added_at"] = "2026-10-01T00:00:00+08:00",
                ["players"] = new JsonArray(new JsonObject { ["name"] = "mia" }),
                ["rules"] = new JsonObject { ["mp_maxrounds"] = "24" },
            });
            store.Update("192.0.2.10|27015", new Dictionary<string, JsonNode?>
            {
                ["online"] = true,
                ["ping_ms"] = 20,
            });
            var reloaded = new ServerStore(path);
            Assert.Single(reloaded.Servers);
            Assert.Equal("mia", reloaded.Servers[0]["players"]![0]!["name"]!.GetValue<string>());
            Assert.Equal(20, reloaded.Servers[0]["ping_ms"]!.GetValue<int>());
            Assert.True(reloaded.Remove("192.0.2.10|27015"));
            Assert.Empty(new ServerStore(path).Servers);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void UpsertKeepsExistingDetailWhenRefreshHasNone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cswatch-upsert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var store = new ServerStore(Path.Combine(folder, "servers.json"));
            store.Upsert(new JsonObject
            {
                ["id"] = "a|1",
                ["host"] = "a",
                ["port"] = 1,
                ["players"] = new JsonArray(new JsonObject { ["name"] = "kept" }),
                ["rules"] = new JsonObject { ["mp_maxrounds"] = "16" },
            });
            store.Upsert(new JsonObject
            {
                ["id"] = "a|1",
                ["host"] = "a",
                ["port"] = 1,
                ["players"] = ServerStore.NullNode(),
                ["rules"] = ServerStore.NullNode(),
                ["ping_ms"] = 8,
            });
            var saved = store.Get("a|1")!;
            Assert.Equal("kept", saved["players"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("16", saved["rules"]!["mp_maxrounds"]!.GetValue<string>());
            Assert.Equal(8, saved["ping_ms"]!.GetValue<int>());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

internal static class Payloads
{
    public static byte[] CString(string text) => Encoding.UTF8.GetBytes(text).Concat(new byte[] { 0 }).ToArray();

    public static byte[] Info(
        int protocol = 17,
        string name = "Dust Server",
        string mapName = "de_dust2",
        string folder = "csgo",
        string game = "Counter-Strike 2",
        int appId = 730,
        int players = 4,
        int maxPlayers = 10,
        int bots = 1,
        byte serverType = (byte)'d',
        byte environment = (byte)'l',
        byte visibility = 0,
        byte vac = 1,
        string version = "1.40.8.8",
        byte edf = 0,
        byte[]? extra = null)
    {
        var body = new List<byte> { 0x49, (byte)protocol };
        body.AddRange(CString(name));
        body.AddRange(CString(mapName));
        body.AddRange(CString(folder));
        body.AddRange(CString(game));
        body.AddRange(BitConverter.GetBytes((ushort)appId));
        body.Add((byte)players);
        body.Add((byte)maxPlayers);
        body.Add((byte)bots);
        body.Add(serverType);
        body.Add(environment);
        body.Add(visibility);
        body.Add(vac);
        body.AddRange(CString(version));
        body.Add(edf);
        if (extra is not null)
        {
            body.AddRange(extra);
        }

        return body.ToArray();
    }

    public static byte[] Players(IEnumerable<(byte Index, string Name, int Score, float Duration)> players)
    {
        var list = players.ToList();
        var body = new List<byte> { 0x44, (byte)list.Count };
        foreach (var (index, name, score, duration) in list)
        {
            body.Add(index);
            body.AddRange(CString(name));
            body.AddRange(BitConverter.GetBytes(score));
            body.AddRange(BitConverter.GetBytes(duration));
        }

        return body.ToArray();
    }

    public static byte[] Rules(IEnumerable<(string Key, string Value)> rules)
    {
        var list = rules.ToList();
        var body = new List<byte> { 0x45 };
        body.AddRange(BitConverter.GetBytes((ushort)list.Count));
        foreach (var (key, value) in list)
        {
            body.AddRange(CString(key));
            body.AddRange(CString(value));
        }

        return body.ToArray();
    }

    public static List<byte[]> Split(byte[] payload, bool reverse = false, bool compressed = false)
    {
        var blob = payload;
        uint ident = 0x12345678;
        if (compressed)
        {
            var compressedBlob = Bzip(payload);
            var header = new byte[8];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), Crc(payload));
            blob = header.Concat(compressedBlob).ToArray();
            ident |= 0x80000000;
        }

        var chunks = new List<byte[]>();
        for (var index = 0; index < blob.Length; index += 16)
        {
            chunks.Add(blob.Skip(index).Take(16).ToArray());
        }

        var packets = new List<byte[]>();
        for (var number = 0; number < chunks.Count; number++)
        {
            var header = new byte[12];
            BinaryPrimitives.WriteInt32LittleEndian(header, -2);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), ident);
            header[8] = (byte)chunks.Count;
            header[9] = (byte)number;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 1248);
            packets.Add(header.Concat(chunks[number]).ToArray());
        }

        if (reverse)
        {
            packets.Reverse();
        }

        return packets;
    }

    public static Func<byte[], IEnumerable<byte[]>> ChallengeHandler(Dictionary<byte, object> responses)
    {
        var challenge = new byte[] { 0x11, 0x22, 0x33, 0x44 };
        return data =>
        {
            if (NeedsChallenge(data))
            {
                return [new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x41 }.Concat(challenge).ToArray()];
            }

            if (data.Length < 5 || !responses.TryGetValue(data[4], out var payload))
            {
                return [];
            }

            if (payload is List<byte[]> packets)
            {
                return packets;
            }

            return [new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }.Concat((byte[])payload).ToArray()];
        };
    }

    private static bool NeedsChallenge(byte[] data)
    {
        if (data.Length < 9)
        {
            return false;
        }

        if (data[4] == 0x54)
        {
            return data[^4] == 0xFF && data[^3] == 0xFF && data[^2] == 0xFF && data[^1] == 0xFF;
        }

        if (data[4] is 0x55 or 0x56)
        {
            return data[5] == 0xFF && data[6] == 0xFF && data[7] == 0xFF && data[8] == 0xFF;
        }

        return false;
    }

    private static byte[] Bzip(byte[] payload)
    {
        using var input = new MemoryStream(payload);
        using var output = new MemoryStream();
        ICSharpCode.SharpZipLib.BZip2.BZip2.Compress(input, output, isStreamOwner: false, level: 9);
        return output.ToArray();
    }

    private static uint Crc(byte[] data)
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
}

internal sealed class FakeServer : IDisposable
{
    private readonly Func<byte[], IEnumerable<byte[]>> _handler;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeServer(Func<byte[], IEnumerable<byte[]>> handler)
    {
        _handler = handler;
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _socket.ReceiveTimeout = 200;
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
        _loop = Task.Run(Run);
    }

    public int Port { get; }
    public List<byte[]> Received { get; } = [];

    private void Run()
    {
        var buffer = new byte[65535];
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                var read = _socket.ReceiveFrom(buffer, ref remote);
                var data = buffer.AsSpan(0, read).ToArray();
                lock (Received)
                {
                    Received.Add(data);
                }

                foreach (var packet in _handler(data))
                {
                    _socket.SendTo(packet, remote);
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Close();
        _loop.Wait(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}


