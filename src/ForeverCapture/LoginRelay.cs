// Login relay: the game logs in to 127.0.0.1 instead of the Battle.net login server, and this relay passes every byte on to the
// real server and back over its own TLS connection (checked the normal Windows way). While relaying it reads two values out of
// the realm join: the game's 32-byte secret and the server's 32-byte secret. Together they are the key data every world
// connection of that login derives its encryption from. Nothing else is kept, and no login traffic is written to disk.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AuthServer.Network;
using Bgs.Protocol;

namespace ForeverCapture;

// Reads the Battle.net RPC frames of one login connection (2-byte big-endian header length, protobuf Header, payload) and reports
// every realm secret that passes: the game's (in its realm ticket request) and the server's (in a realm join answer). It does not
// pair them; the decoder tries each pair and the first encrypted packet's tag proves the right one. It only ever looks at
// copies: a frame it cannot read is skipped, and framing it cannot follow switches the tap off, never the relay.
// shortcut: sees secrets sent over the login connection only. A realm or ruleset switch made later from inside the game travels
// over the world connection instead; read those from the decrypted world stream if a capture ever needs them.
internal sealed class JoinTap
{
    private readonly Action<string> log;
    private readonly Action<byte[]> gameSecret;
    private readonly Action<byte[], string[]> joinSecret;
    private readonly Cutter fromClient = new(), fromServer = new();
    public JoinTap(Action<string> onLog, Action<byte[]> onGameSecret, Action<byte[], string[]> onJoinSecret) { log = onLog; gameSecret = onGameSecret; joinSecret = onJoinSecret; }

    public void FromClient(byte[] data, int count) => fromClient.Feed(data, count, log, (header, payload) =>
    {
        if (header.ServiceId == 0xFE) return;
        var attributes = BgsRealmV2.ReadAttributes(payload);
        foreach (var command in attributes.Keys.Where(k => k.StartsWith("Command_", StringComparison.Ordinal))) log("game asks " + command);
        if (!attributes.TryGetValue("Param_ClientInfo", out var info) || info.Blob == null) return;
        using var document = JsonDocument.Parse(Json(info.Blob));
        var secret = document.RootElement.GetProperty("info").GetProperty("secret");
        var bytes = secret.ValueKind == JsonValueKind.String ? Convert.FromBase64String(secret.GetString()) : secret.EnumerateArray().Select(v => v.GetByte()).ToArray();
        if (bytes.Length != 32) { log($"game secret has {bytes.Length} bytes, expected 32"); return; }
        gameSecret(bytes);
    });

    public void FromServer(byte[] data, int count) => fromServer.Feed(data, count, log, (header, payload) =>
    {
        if (header.ServiceId != 0xFE) return;
        var attributes = BgsRealmV2.ReadAttributes(payload);
        if (!attributes.TryGetValue("Param_JoinSecret", out var secret) || secret.Blob == null) return;
        if (secret.Blob.Length != 32) { log($"join secret has {secret.Blob.Length} bytes, expected 32"); return; }
        var servers = Array.Empty<string>();
        try
        {
            // Compressed blob: int32 plain length, then zlib.
            var addresses = attributes["Param_ServerAddresses"].Blob;
            using var zlib = new ZLibStream(new MemoryStream(addresses, 4, addresses.Length - 4), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            zlib.CopyTo(plain);
            using var document = JsonDocument.Parse(Json(plain.ToArray()));
            servers = document.RootElement.GetProperty("families").EnumerateArray().SelectMany(f => f.GetProperty("addresses").EnumerateArray())
                .Select(a => $"{a.GetProperty("ip").GetString()}:{a.GetProperty("port").GetInt32()}").ToArray();
        }
        // The addresses are only shown to the user; the secret is what matters.
        catch (Exception error) { log($"the join answer's server addresses could not be read ({error.GetType().Name})"); }
        joinSecret(secret.Blob, servers);
    });

    // Realm blobs are "<Name>:<json>" with a trailing zero.
    private static string Json(byte[] blob)
    {
        var text = Encoding.UTF8.GetString(blob).TrimEnd('\0');
        return text[(text.IndexOf(':') + 1)..];
    }

    private sealed class Cutter
    {
        private byte[] buffer = new byte[4096];
        private int length;
        private bool off;
        public void Feed(byte[] data, int count, Action<string> log, Action<Header, byte[]> frame)
        {
            if (off) return;
            try
            {
                if (length + count > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + count));
                Buffer.BlockCopy(data, 0, buffer, length, count);
                length += count;
                var at = 0;
                while (length - at >= 2)
                {
                    var headerLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(at));
                    if (length - at < 2 + headerLength) break;
                    var header = Header.Parser.ParseFrom(buffer, at + 2, headerLength);
                    var size = checked((int)header.Size);
                    if (size > 1 << 22) throw new InvalidDataException("Battle.net frame too large.");
                    if (length - at < 2 + headerLength + size) break;
                    // Most frames are not realm requests and do not read as one: skip those, keep watching.
                    var payload = buffer.AsSpan(at + 2 + headerLength, size).ToArray();
                    try { frame(header, payload); }
                    catch (Exception error) { if (payload.AsSpan().IndexOf("Param_"u8) >= 0) log($"a realm frame could not be read ({error.GetType().Name})"); }
                    at += 2 + headerLength + size;
                }
                Buffer.BlockCopy(buffer, at, buffer, 0, length - at);
                length -= at;
            }
            // The tap must never disturb the login it is watching: framing it cannot follow ends the watching, nothing else.
            catch (Exception error) { off = true; buffer = Array.Empty<byte>(); length = 0; log($"stopped reading this connection's frames ({error.GetType().Name}); it is still relayed"); }
        }
    }
}

internal sealed class LoginRelay : IDisposable
{
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly string upstreamHost;
    private readonly int upstreamPort;
    private readonly Action<string> log;
    private readonly Action<byte[]> gameSecret;
    private readonly Action<byte[], string[]> joinSecret;
    private int connections;

    public LoginRelay(X509Certificate2 loopbackCertificate, string host, int hostPort, int listenPort, Action<string> onLog, Action<byte[]> onGameSecret, Action<byte[], string[]> onJoinSecret)
    {
        certificate = loopbackCertificate; upstreamHost = host; upstreamPort = hostPort; log = onLog; gameSecret = onGameSecret; joinSecret = onJoinSecret;
        listener = new TcpListener(IPAddress.Loopback, listenPort);
    }

    public void Start() { listener.Start(); _ = Accept(); }
    public void Dispose() => listener.Stop();

    private async Task Accept()
    {
        while (true)
        {
            TcpClient game;
            try { game = await listener.AcceptTcpClientAsync(); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Serve(game);
        }
    }

    private async Task Serve(TcpClient game)
    {
        using var upstream = new TcpClient { NoDelay = true };
        var id = Interlocked.Increment(ref connections);
        void Say(string line) => log($"Login connection {id}: {line}");
        try
        {
            game.NoDelay = true;
            using var gameTls = new SslStream(game.GetStream());
            await gameTls.AuthenticateAsServerAsync(certificate);
            await upstream.ConnectAsync(upstreamHost, upstreamPort);
            using var upstreamTls = new SslStream(upstream.GetStream());
            // Normal validation: the real server's certificate must chain to a root Windows trusts and carry this host name.
            await upstreamTls.AuthenticateAsClientAsync(upstreamHost);
            Say($"relayed to {upstreamHost}:{upstreamPort}.");
            var tap = new JoinTap(Say, gameSecret, joinSecret);
            var up = Pump(gameTls, upstreamTls, tap.FromClient);
            var down = Pump(upstreamTls, gameTls, tap.FromServer);
            await Task.WhenAny(up, down);
            game.Close(); upstream.Close();
            try { await Task.WhenAll(up, down); } catch (Exception error) when (error is IOException or ObjectDisposedException or SocketException) { }
            Say("closed.");
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException or ObjectDisposedException)
        {
            Say($"failed ({error.GetType().Name}): {error.Message}");
        }
        finally { game.Dispose(); }
    }

    // Forwards first, looks second: the tap never delays a byte.
    private static async Task Pump(Stream from, Stream to, Action<byte[], int> tap)
    {
        var buffer = new byte[16384];
        int read;
        while ((read = await from.ReadAsync(buffer)) > 0)
        {
            await to.WriteAsync(buffer.AsMemory(0, read));
            await to.FlushAsync();
            tap(buffer, read);
        }
    }
}
