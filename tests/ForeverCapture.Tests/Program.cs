// Round trip: the sandbox server's own code writes a login and two world connections with game objects; the capture code
// (join tap, pcapng reader, TCP reassembly, key search, decryption, packet inflation, object scanner) must read them back.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using AuthServer.Network;
using Bgs.Protocol;
using ForeverCapture;
using Google.Protobuf;

static void Check(bool ok, string what) { if (!ok) throw new Exception("FAILED: " + what); }
static byte[] Pack(Action<CodedOutputStream> fill) { using var memory = new MemoryStream(); using (var output = new CodedOutputStream(memory, true)) { fill(output); output.Flush(); } return memory.ToArray(); }
static byte[] Attribute(string name, byte[] variant) => Pack(o => { o.WriteTag(1, WireFormat.WireType.LengthDelimited); o.WriteString(name); o.WriteTag(2, WireFormat.WireType.LengthDelimited); o.WriteBytes(ByteString.CopyFrom(variant)); });
static byte[] Request(params byte[][] attributes) => Pack(o => { foreach (var a in attributes) { o.WriteTag(1, WireFormat.WireType.LengthDelimited); o.WriteBytes(ByteString.CopyFrom(a)); } });
static byte[] Blob(byte[] blob) => Pack(o => { o.WriteTag(5, WireFormat.WireType.LengthDelimited); o.WriteBytes(ByteString.CopyFrom(blob)); });
static byte[] Number(ulong value) => Pack(o => { o.WriteTag(6, WireFormat.WireType.Varint); o.WriteUInt64(value); });
static byte[] ClientFrame(uint token, byte[] payload)
{
    var header = new Header { ServiceId = 0, ServiceHash = BgsRealmV2.ServiceHash, MethodId = 1, Token = token, Size = (uint)payload.Length }.ToByteArray();
    return new[] { (byte)(header.Length >> 8), (byte)header.Length }.Concat(header).Concat(payload).ToArray();
}

// 1. Login: a ticket request and a realm join answered by the sandbox's realm service, seen through the tap in small pieces.
var random = new Random(70888);
var clientSecret = new byte[32]; random.NextBytes(clientSecret); clientSecret[0] |= 1;
var realmState = new BgsRealmState();
// The realm service accepts only the client build the sandbox speaks: its ClientBuild where it has one, 70338 before that.
var sandboxBuild = typeof(BgsRealmV2).GetField("ClientBuild")?.GetValue(null) ?? 70338u;
var ticket = Request(Attribute("Command_RealmListTicketRequest_v1_wowf", Array.Empty<byte>()),
    Attribute("Param_Identity", Blob(Encoding.UTF8.GetBytes("JSONRealmListTicketIdentity:{\"gameAccountID\":1,\"gameAccountRegion\":2}\0"))),
    Attribute("Param_ClientInfo", Blob(Encoding.UTF8.GetBytes("JSONRealmListTicketClientInformation:{\"info\":{\"platformType\":1,\"clientArch\":2,\"type\":3,\"version\":{\"versionBuild\":"
        + sandboxBuild + "},\"secret\":[" + string.Join(",", clientSecret) + "]}}\0"))));
var join = Request(Attribute("Command_RealmJoinRequest_v1_wowf", Array.Empty<byte>()), Attribute("Param_RealmAddress", Number(BgsRealmV2.RealmAddress)));
var ticketReply = BgsRealmV2.Reply(1, ticket, realmState, true, 8085);
var joinReply = BgsRealmV2.Reply(1, join, realmState, true, 8085);
Check(ticketReply.status == 0 && joinReply.status == 0, "the sandbox realm service refused the test requests");
var gameSecrets = new List<byte[]>(); var joinSecrets = new List<(byte[] Secret, string[] Servers)>(); var tapLog = new List<string>();
var tap = new JoinTap(tapLog.Add, gameSecrets.Add, (secret, servers) => joinSecrets.Add((secret, servers)));
void Feed(byte[] bytes, Action<byte[], int> into) { for (var at = 0; at < bytes.Length; at += 7) { var piece = bytes.AsSpan(at, Math.Min(7, bytes.Length - at)).ToArray(); into(piece, piece.Length); } }
Feed(ClientFrame(1, Pack(o => { o.WriteTag(1, WireFormat.WireType.LengthDelimited); o.WriteString("WoW"); })), tap.FromClient);   // a frame that is no realm request
Feed(ClientFrame(2, ticket), tap.FromClient);
Feed(BgsFrameWriter.Reply(2, ticketReply.payload), tap.FromServer);
Feed(ClientFrame(3, join), tap.FromClient);
Feed(BgsFrameWriter.Reply(3, joinReply.payload), tap.FromServer);
var keyData = ForeverJoinRegistry.Find(BgsRealmV2.DemoGameAccount);
Check(gameSecrets.Count == 1 && joinSecrets.Count == 1 && gameSecrets[0].Concat(joinSecrets[0].Secret).SequenceEqual(keyData), "the tap did not report the realm join's two secrets");
Check(joinSecrets[0].Servers.SequenceEqual(new[] { "127.0.0.1:8085" }), "the tap did not read the world server address");
Check(tapLog.Count(l => l.StartsWith("game asks Command_Realm", StringComparison.Ordinal)) == 2, "the tap did not log the two realm requests");

// 2. World: a realm connection and an instance connection, written with the server's framing, keys and object writer.
static byte[] Challenge(byte[] serverChallenge) { var c = new byte[65]; serverChallenge.CopyTo(c, 32); c[64] = 1; return c; }
byte[] Bytes(int count) { var b = new byte[count]; random.NextBytes(b); return b; }
byte[] realmServerChallenge = Bytes(32), realmLocal = Bytes(32), instanceServerChallenge = Bytes(32), instanceLocal = Bytes(32);
var sessionKey = ForeverWorldKeys.SessionKey(keyData, realmServerChallenge, realmLocal);
var realmKey = ForeverWorldKeys.EncryptKey(sessionKey, realmLocal, realmServerChallenge);
var instanceKey = ForeverWorldKeys.EncryptKey(sessionKey, instanceLocal, instanceServerChallenge);

var ticketJson = Encoding.UTF8.GetBytes("{\"gameAccount\":\"sandbox\",\"platform\":1,\"type\":3,\"clientArch\":2}");
var authSession = new byte[81 + ticketJson.Length];
realmLocal.CopyTo(authSession, 20); BinaryPrimitives.WriteUInt32LittleEndian(authSession.AsSpan(77), (uint)ticketJson.Length); ticketJson.CopyTo(authSession, 81);
var continued = new byte[80]; instanceLocal.CopyTo(continued, 8);

var spawns = new[]
{
    new ForeverGameObjects.Spawn(ForeverGameObjects.Guid(2991, 529161, 1), 529161, 31, 3295.6016f, 1910.125f, 837.5376f, 1.25f, ForeverGameObjects.Rotation(1.25f, 0.2f, -0.1f), 1.4f),
    new ForeverGameObjects.Spawn(ForeverGameObjects.Guid(2991, 710486, 0x12345), 710486, 31, -14297.2f, 530.9f, 8.8f, 3.1f, ForeverGameObjects.Rotation(3.1f), 1f, 0x20, 0),
    new ForeverGameObjects.Spawn(ForeverGameObjects.Guid(2991, 457387, 77), 457387, 9001, 17.3646f, -82.4861f, 490.6032f, 2.927089f, ForeverGameObjects.Rotation(2.9f, -1.1f, 0.6f), 0.5f),
    new ForeverGameObjects.Spawn(ForeverGameObjects.Guid(2991, 13965, 0xFFFFFFFFFF), 13965, 411, -100f, 250f, -30f, 0f, ForeverGameObjects.Rotation(0f), 2f),
};
byte[] Update(params ForeverGameObjects.Spawn[] list) => ForeverGameObjects.Packets(2991, list.Select(ForeverGameObjects.CreateBlock).ToList(), Array.Empty<byte[]>()).Single();

using var packed = new MemoryStream();
using var deflater = new DeflateStream(packed, CompressionLevel.Fastest, true);
byte[] Compressed(uint opcode, byte[] payload)   // the server's compressed-packet body: one deflate stream, sync-flushed per packet
{
    var inner = new byte[4 + payload.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(inner, opcode); payload.CopyTo(inner, 4);
    packed.SetLength(0);
    deflater.Write(inner); deflater.Flush();
    var bytes = packed.ToArray();
    var body = new byte[12 + bytes.Length];
    BinaryPrimitives.WriteInt32LittleEndian(body, inner.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), WorldDecoder.Adler(0x9827D8F1, inner));
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), WorldDecoder.Adler(0x9827D8F1, bytes));
    bytes.CopyTo(body, 12);
    return body;
}

byte[] ServerStream(byte[] serverChallenge, byte[] key, params (uint opcode, byte[] payload)[] packets)
{
    using var crypt = new ForeverPacketCrypt();
    using var stream = new MemoryStream();
    stream.Write(Encoding.ASCII.GetBytes(ForeverWorldWire.ServerInitializer));
    stream.Write(ForeverWorldWire.Seal(crypt, ForeverWorldWire.AuthChallengeOpcode, Challenge(serverChallenge)));
    stream.Write(ForeverWorldWire.Seal(crypt, ForeverWorldPackets.EnterEncryptedModeOpcode, ForeverWorldKeys.EnterEncryptedMode(key, ForeverWorldKeys.InteropSigningSeed)));
    crypt.Enable(key);
    foreach (var (opcode, payload) in packets) stream.Write(ForeverWorldWire.Seal(crypt, opcode, payload));
    return stream.ToArray();
}
byte[] ClientStream(uint opcode, byte[] payload, params byte[][] sealedFrames) => Encoding.ASCII.GetBytes(ForeverWorldWire.ClientInitializer)
    .Concat(ForeverWorldWire.Pack(opcode, payload)).Concat(ForeverWorldWire.Pack(ForeverWorldPackets.EnterEncryptedModeAckOpcode, Array.Empty<byte>()))
    .Concat(sealedFrames.SelectMany(f => f)).Concat(Bytes(300)).ToArray();
// What the game sends once encryption is on: its own direction word, counting from the connection's first frame (these start at 2).
byte[] ClientSeal(byte[] key, ulong counter, uint opcode, byte[] payload)
{
    var plain = new byte[4 + payload.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(plain, opcode); payload.CopyTo(plain, 4);
    var iv = new byte[12];
    BinaryPrimitives.WriteUInt64LittleEndian(iv, counter); BinaryPrimitives.WriteUInt32LittleEndian(iv.AsSpan(8), 0x544E4C43);
    var wire = new byte[16 + plain.Length]; var cipher = new byte[plain.Length]; var tag = new byte[12];
    using var aes = new AesGcm(key, 12);
    aes.Encrypt(iv, plain, cipher, tag);
    BinaryPrimitives.WriteUInt32LittleEndian(wire, (uint)plain.Length); tag.CopyTo(wire, 4); cipher.CopyTo(wire, 16);
    return wire;
}
// A movement packet: the player's packed GUID, flags, time, position and facing, then whatever else the game adds.
byte[] Move(float x, float y, float z) => new ForeverPacketWriter().Guid(ForeverCharacterPackets.PlayerGuid(7)).U64(1).U32(1000).F32(x).F32(y).F32(z).F32(0.5f).Raw(Bytes(20)).ToArray();
// A bundle: uint16 payload size, uint32 opcode, payload, repeated.
byte[] Bundle(params (uint opcode, byte[] payload)[] inner) => inner.SelectMany(p => BitConverter.GetBytes((ushort)p.payload.Length).Concat(BitConverter.GetBytes(p.opcode)).Concat(p.payload)).ToArray();

var realm = new WorldConnection("realm", ClientStream(ForeverWorldWire.AuthSessionOpcode, authSession),
    ServerStream(realmServerChallenge, realmKey, (ForeverWorldPackets.AuthResponseOpcode, ForeverWorldPackets.AuthResponseOk(1)), (ForeverWorldPackets.PongOpcode, ForeverWorldPackets.Pong(5))), true);
// The path: a step under three yards is dropped, a jump starts a new stretch, and a packet that is no movement is ignored.
var instance = new WorldConnection("instance", ClientStream(ForeverLoginPackets.AuthContinuedSessionOpcode, continued,
        ClientSeal(instanceKey, 2, 0x420064, Move(100, 200, 30)), ClientSeal(instanceKey, 3, 0x420064, Move(101, 200, 30)),
        ClientSeal(instanceKey, 4, 0x440024, Move(500, 500, 30)), ClientSeal(instanceKey, 5, 0x42006A, Move(110, 200, 31)),
        ClientSeal(instanceKey, 6, 0x420064, Move(2000, 500, 10))),
    ServerStream(instanceServerChallenge, instanceKey,
        (0x4E000D, Bundle((0x470099, Bytes(20)), (ForeverPlayerObject.UpdateObjectOpcode, Update(spawns[0], spawns[1])))),
        (0x4E000E, Compressed(ForeverPlayerObject.UpdateObjectOpcode, Update(spawns[2]))),
        (0x470099, Bytes(700)),
        (0x4E000E, Compressed(ForeverPlayerObject.UpdateObjectOpcode, Update(spawns[3]))),
        (ForeverPlayerObject.UpdateObjectOpcode, Update(spawns[0]))), true);   // the same object seen again

void CheckObjects(IEnumerable<WorldConnection> connections, string how)
{
    var (objects, track, stats) = WorldDecoder.Decode(connections, new[] { Bytes(64), keyData });
    Check(track.Count == 2 && track.All(s => s.Map == 2991) && stats.TrackPoints == 3 && stats.TrackWithoutMap == 0 &&
        track[0].Points.Select(p => string.Join(",", p)).SequenceEqual(new[] { "100,200,30", "110,200,31" }) && string.Join(",", track[1].Points[0]) == "2000,500,10",
        $"{how}: the character's path was not read from the game's movement packets, on the map of the connection's update packets");
    Check(stats.Connections == 2 && stats.Decrypted == 2 && stats.NoKey == 0, $"{how}: not both connections were decrypted");
    Check(stats.CompressedFrames == 2 && stats.CompressionErrors == 0, $"{how}: the compressed packets were not unpacked");
    Check(objects.Count == spawns.Length && stats.Sightings == 5 && stats.LayoutInexact == 0, $"{how}: expected {spawns.Length} objects, got {objects.Count} ({stats.Sightings} sightings, {stats.LayoutInexact} inexact)");
    foreach (var s in spawns)
    {
        var o = objects.Single(x => x.Entry == (int)s.Entry);
        var sign = s.Rotation.W < 0 ? -1 : 1;
        var expected = new[] { s.Rotation.X * sign, s.Rotation.Y * sign, s.Rotation.Z * sign, s.Rotation.W * sign };
        Check(o.Map == 2991 && o.Display == s.Display && o.Type == s.Type && o.Flags == s.Flags && o.State == 1 && o.Scale == s.Scale, $"{how}: object {s.Entry} fields differ");
        Check(o.X == s.X && o.Y == s.Y && o.Z == s.Z && o.Facing == s.Facing, $"{how}: object {s.Entry} position differs");
        // x, y and z come back to the packing's 2^-20 steps; w is rebuilt from them, so it is coarser near half turns.
        var off = o.Rotation.Zip(expected, (a, b) => MathF.Abs(a - b)).ToArray();
        Check(off[0] < 1e-5f && off[1] < 1e-5f && off[2] < 1e-5f && off[3] < 1e-3f, $"{how}: object {s.Entry} rotation differs");
        Check(o.LayoutExact && o.ParentRotation.SequenceEqual(new[] { 0f, 0f, 0f, 1f }), $"{how}: object {s.Entry} values were not read to the end");
    }
}
CheckObjects(new[] { realm, instance }, "streams");
var wrongKeys = WorldDecoder.Decode(new[] { realm, instance }, new[] { Bytes(64) });
Check(wrongKeys.Stats.NoKey == 2 && wrongKeys.Objects.Count == 0, "a wrong key must decrypt nothing");

// 3. Network: the same connections as TCP packets in a pcapng file, out of order, repeated, overlapping, across a sequence wrap,
// one over IPv6, next to other traffic.
var packets = new List<byte[]>();
static byte[] Tcp(IPAddress source, int sourcePort, IPAddress target, int targetPort, uint sequence, byte flags, byte[] payload)
{
    var v6 = source.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
    var ip = new byte[v6 ? 40 : 20];
    if (v6) { ip[0] = 0x60; BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(4), (ushort)(20 + payload.Length)); ip[6] = 6; ip[7] = 64; source.GetAddressBytes().CopyTo(ip, 8); target.GetAddressBytes().CopyTo(ip, 24); }
    else { ip[0] = 0x45; BinaryPrimitives.WriteUInt16BigEndian(ip.AsSpan(2), (ushort)(40 + payload.Length)); ip[8] = 64; ip[9] = 6; source.GetAddressBytes().CopyTo(ip, 12); target.GetAddressBytes().CopyTo(ip, 16); }
    var tcp = new byte[20];
    BinaryPrimitives.WriteUInt16BigEndian(tcp, (ushort)sourcePort); BinaryPrimitives.WriteUInt16BigEndian(tcp.AsSpan(2), (ushort)targetPort);
    BinaryPrimitives.WriteUInt32BigEndian(tcp.AsSpan(4), sequence); tcp[12] = 0x50; tcp[13] = flags;
    var ethernet = new byte[14]; ethernet[12] = (byte)(v6 ? 0x86 : 0x08); ethernet[13] = (byte)(v6 ? 0xDD : 0x00);
    var frame = ethernet.Concat(ip).Concat(tcp).Concat(payload);
    return (payload.Length < 6 && !v6 ? frame.Concat(new byte[6 - payload.Length]) : frame).ToArray();   // short frames carry Ethernet padding
}
void Connection(IPAddress pc, int pcPort, IPAddress server, int serverPort, byte[] fromClient, byte[] fromServer, uint clientStart, uint serverStart, bool withStart = true)
{
    var mine = new List<byte[]>();
    if (withStart) { mine.Add(Tcp(pc, pcPort, server, serverPort, clientStart, 0x02, Array.Empty<byte>())); mine.Add(Tcp(server, serverPort, pc, pcPort, serverStart, 0x12, Array.Empty<byte>())); }
    var data = new List<byte[]>();
    void Split(byte[] bytes, IPAddress a, int ap, IPAddress b, int bp, uint start)
    {
        for (var at = 0; at < bytes.Length;)
        {
            var size = Math.Min(random.Next(1, 1400), bytes.Length - at);
            data.Add(Tcp(a, ap, b, bp, unchecked(start + 1 + (uint)at), 0x18, bytes.AsSpan(at, size).ToArray()));
            if (random.Next(6) == 0 && at > 40) data.Add(Tcp(a, ap, b, bp, unchecked(start + 1 + (uint)at - 30), 0x18, bytes.AsSpan(at - 30, Math.Min(60, bytes.Length - at + 30)).ToArray()));   // overlapping resend
            if (random.Next(8) == 0) data.Add(data[^1]);                                                                                             // exact repeat
            at += size;
        }
    }
    Split(fromClient, pc, pcPort, server, serverPort, clientStart); var clientPackets = data.Count;
    Split(fromServer, server, serverPort, pc, pcPort, serverStart);
    // The game's first packet stays first; the server's packets arrive in a locally shuffled order.
    for (var i = clientPackets; i + 3 < data.Count; i += 3) if (random.Next(3) == 0) (data[i], data[i + 2]) = (data[i + 2], data[i]);
    mine.AddRange(data);
    packets.AddRange(mine);
}
IPAddress pc4 = IPAddress.Parse("192.168.1.20"), server4 = IPAddress.Parse("37.244.28.10"), pc6 = IPAddress.Parse("2a02:aa1::20"), server6 = IPAddress.Parse("2a01:111::77");
Connection(pc4, 50123, IPAddress.Parse("93.184.216.34"), 80, Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\nAccept: */*\r\n\r\n"), Bytes(5000), 1000, 2000);
Connection(pc4, 50200, server4, 3724, realm.ClientBytes, realm.ServerBytes, 0x10000000, 0xFFFFFF00);
Connection(pc4, 50300, server4, 3724, Bytes(900), Bytes(900), 7, 9, withStart: false);   // already open when the recording began
Connection(pc6, 50400, server6, 3724, instance.ClientBytes, instance.ServerBytes, 0xFFFFFFF0, 0x7FFFFF00);

var pcapPath = Path.Combine(Path.GetTempPath(), "forever-capture-test-" + Guid.NewGuid().ToString("N") + ".pcapng");
var capturePath = Path.ChangeExtension(pcapPath, ".fwcap");
try
{
    using (var writer = new BinaryWriter(File.Create(pcapPath)))
    {
        writer.Write(0x0A0D0D0Au); writer.Write(28); writer.Write(0x1A2B3C4Du); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write(-1L); writer.Write(28);   // section header
        writer.Write(1u); writer.Write(20); writer.Write((ushort)1); writer.Write((ushort)0); writer.Write(0); writer.Write(20);                                          // one Ethernet interface
        foreach (var packet in packets)
        {
            var padded = (packet.Length + 3) & ~3;
            writer.Write(6u); writer.Write(32 + padded); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(packet.Length); writer.Write(packet.Length);
            writer.Write(packet); writer.Write(new byte[padded - packet.Length]); writer.Write(32 + padded);
        }
    }
    var extracted = WorldCapture.Extract(pcapPath);
    Check(extracted.Count == 2, $"expected 2 world connections in the recording, found {extracted.Count}");
    Check(extracted[0].Server == "37.244.28.10:3724" && extracted[1].Server == "2a01:111::77:3724", "the world connections' servers were not kept");
    Check(extracted.All(c => c.Complete), "a reassembled connection was reported incomplete");
    Check(extracted[0].ServerBytes.SequenceEqual(realm.ServerBytes) && extracted[1].ServerBytes.SequenceEqual(instance.ServerBytes), "the server streams were not reassembled exactly");
    Check(extracted[0].ClientBytes.SequenceEqual(realm.ClientBytes) && extracted[1].ClientBytes.SequenceEqual(instance.ClientBytes), "the client streams were not reassembled exactly");
    WorldCapture.Write(capturePath, extracted);
    CheckObjects(WorldCapture.Read(capturePath), "recording");

    // The live recording's own file writer gives the same connections back, and the streams read live are complete at any
    // moment: a snapshot halfway is the start of the final streams.
    var written = Path.ChangeExtension(pcapPath, ".live.pcapng");
    var live = new WorldCapture.TcpStreams();
    List<WorldConnection> halfway = null;
    using (var pcapWriter = new PcapNgWriter(written))
    {
        var nic = pcapWriter.AddInterface(1);
        for (var i = 0; i < packets.Count; i++)
        {
            pcapWriter.Write(nic, 1_700_000_000_000_000L + i, packets[i], packets[i].Length);
            PcapNg.Packet(packets[i], 1, live.Add);
            if (i == packets.Count / 2) halfway = live.Connections();
        }
    }
    var reread = WorldCapture.Extract(written);
    var final = live.Connections();
    File.Delete(written);
    Check(reread.Count == 2 && reread.Zip(extracted).All(p => p.First.ServerBytes.SequenceEqual(p.Second.ServerBytes) && p.First.ClientBytes.SequenceEqual(p.Second.ClientBytes)),
        "the live recording's pcapng file does not give the same connections back");
    Check(final.Zip(extracted).All(p => p.First.ServerBytes.SequenceEqual(p.Second.ServerBytes)) &&
        halfway.All(h => final.Any(f => f.Server == h.Server && f.ServerBytes.AsSpan().StartsWith(h.ServerBytes))),
        "the streams read live are not the start of the final streams");
}
finally { File.Delete(pcapPath); File.Delete(capturePath); }

// The map window's server (LocalMap): it never serves a file outside the site's folders, adds its script to the page, and
// builds a map's coverage and objects from the capture folders.
{
    var root = Path.Combine(Path.GetTempPath(), "forever-capture-map-" + Guid.NewGuid().ToString("N"));
    try
    {
        Check(LocalMap.Resolve(root, "/js/map.js") == Path.Combine(root, "js", "map.js") && LocalMap.Resolve(root, "/../secret.txt") == null &&
            LocalMap.Resolve(root, "/js/../../secret.txt") == null && LocalMap.Resolve(root, "/C:/Windows/win.ini") == null && LocalMap.Resolve(root, "/a\0b") == null &&
            LocalMap.Resolve(root, "/js\\..\\..\\secret.txt") == null,
            "the map server resolves only paths inside its folder");
        Check(LocalMap.Inject("<html><body><p>x</p></body></html>", "<script></script>") == "<html><body><p>x</p><script></script></body></html>",
            "the map server's script goes at the end of the page's body");
        void Capture(string name, string objects, string track)
        {
            Directory.CreateDirectory(Path.Combine(root, name));
            File.WriteAllText(Path.Combine(root, name, "gameobjects.json"), objects);
            if (track != null) File.WriteAllText(Path.Combine(root, name, "track.json"), track);
        }
        const string Q = "\"rotation\":[0,0,0.5,0.866]";
        Capture("20261116T181500Z", "{\"objects\":[{\"map\":2959,\"entry\":1,\"display\":8096,\"x\":304.19,\"y\":374.33,\"z\":90.7," + Q + ",\"scale\":1}," +
            "{\"map\":0,\"entry\":2,\"display\":31,\"x\":1,\"y\":2,\"z\":3," + Q + ",\"scale\":1}]}",
            "{\"segments\":[{\"map\":2959,\"points\":[[332.24,402,69.1],[337.2,418.5,65.7]]}]}");
        Capture("20261117T153313Z", "{\"objects\":[{\"map\":2959,\"entry\":1,\"display\":8096,\"x\":304.191,\"y\":374.329,\"z\":90.7," + Q + ",\"scale\":1.5}," +
            "{\"map\":2959,\"entry\":3,\"display\":8096,\"x\":260,\"y\":383.7,\"z\":92.6," + Q + ",\"scale\":1}]}", null);
        Capture("broken", "{ not json", null);
        var coverage = System.Text.Encoding.UTF8.GetString(LocalMap.Coverage(root, 2959));
        Check(coverage.Contains("\"objects\":[[260,383.7],[304.2,374.3]]") && coverage.Contains("\"tracks\":[[332.2,402,337.2,418.5]]"),
            "a map's coverage is every capture's objects once and the paths, an unreadable capture left out: " + coverage);
        var placed = LocalMap.Placed(root, 2959);
        Check(placed.Count == 2 && placed.Single(p => p.X > 300).Scale == 1.5 && placed.All(p => p.Rotation.Length == 4 && p.Display == 8096),
            "the same object from two captures is one, the latest sighting");

        // An M2 with two skins, a texture of its own, an animation and a skeleton; a second one with a creature skin.
        static byte[] Chunk(string tag, byte[] body) => System.Text.Encoding.ASCII.GetBytes(tag).Concat(BitConverter.GetBytes(body.Length)).Concat(body).ToArray();
        static byte[] Ints(params int[] values) => values.SelectMany(BitConverter.GetBytes).ToArray();
        static byte[] Md20(params int[] textureTypes)
        {
            var body = new byte[0x58 + textureTypes.Length * 16];
            "MD20"u8.CopyTo(body);
            BitConverter.GetBytes(textureTypes.Length).CopyTo(body, 0x50); BitConverter.GetBytes(0x58).CopyTo(body, 0x54);
            for (var i = 0; i < textureTypes.Length; i++) BitConverter.GetBytes(textureTypes[i]).CopyTo(body, 0x58 + i * 16);
            return body;
        }
        var m2 = Chunk("MD21", Md20(0)).Concat(Chunk("SFID", Ints(11, 12))).Concat(Chunk("TXID", Ints(21))).Concat(Chunk("AFID", Ints(0, 31))).Concat(Chunk("SKID", Ints(41))).ToArray();
        var files = LocalMap.M2Files(m2, out var outside);
        var skel = LocalMap.M2Files(Chunk("SKPD", Ints(0, 0, 51)).Concat(Chunk("AFID", Ints(0, 52))).ToArray(), out _);
        LocalMap.M2Files(Chunk("MD21", Md20(0, 11)).Concat(Chunk("TXID", Ints(0, 22))).ToArray(), out var creature);
        Check(files.SequenceEqual(new[] { 11, 12, 21, 31, 41 }) && !outside && skel.SequenceEqual(new[] { 51, 52 }) && creature,
            "a model's files are its skins, textures, animations and skeleton (and a skeleton's parent and animations); a creature skin is told apart");
        Check(LocalMap.WmoOnly(Chunk("MVER", Ints(18)).Concat(Chunk("MPHD", Ints(1, 0))).ToArray()) && !LocalMap.WmoOnly(Chunk("MVER", Ints(18)).Concat(Chunk("MPHD", Ints(0x80, 0))).ToArray()),
            "a WDT of one building (a dungeon) is told apart from a terrain map's");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

// Npcap on this PC, only when asked for (it needs the network): HTTPS traffic to one site is recorded without
// administrator rights and reads back from the file.
if (Environment.GetEnvironmentVariable("FOREVER_CAPTURE_NPCAP_TEST") == "1")
{
    var probe = Path.Combine(Path.GetTempPath(), "forever-capture-npcap-" + Guid.NewGuid().ToString("N") + ".pcapng");
    var seen = 0;
    using (var capture = LiveCapture.TryStart(probe, "tcp port 443", (_, _, _, _, _, _, _) => Interlocked.Increment(ref seen), out var reason))
    {
        Check(capture != null, "Npcap did not record: " + reason);
        using var http = new HttpClient();
        await http.GetStringAsync("https://example.com/");
        await Task.Delay(1500);
    }
    var fromFile = 0;
    PcapNg.ReadTcp(probe, (_, _, _, _, _, _, _) => fromFile++);
    File.Delete(probe);
    Check(seen > 5 && fromFile == seen, $"Npcap recorded {seen} packets live and {fromFile} in its file");
    Console.WriteLine($"Npcap: {seen} packets recorded live and read back from the file.");
}

Console.WriteLine("ForeverCapture round trip passed: join tap, pcapng + TCP reassembly, key search, decryption, inflation, " + spawns.Length + " game objects, the character's path.");
