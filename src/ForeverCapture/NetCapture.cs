// Network recording: Windows' own packet monitor (pktmon) writes every TCP packet the PC's network adapters see; afterwards only
// the game's world connections are kept (world.fwcap) and the raw recording is deleted. Nothing touches the game process.
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;

namespace ForeverCapture;

// pktmon needs administrator rights, so each call shows one Windows prompt; the game itself is started without them.
internal static class PacketMonitor
{
    // Every world connection of the first live capture (realm and instance servers, at four hosting providers) used this port.
    public const int WorldPort = 3724;

    public static void Start(string folder, bool allPorts)
    {
        var etl = Path.Combine(folder, "net.etl");
        // Stops a leftover session, replaces pktmon's filter list with the world port (or all TCP), records whole packets at the
        // adapters only. All TCP is the fallback if a world server ever uses another port: it grows by more than 100 MB a
        // minute, and past --file-size the oldest packets, the key exchange among them, are overwritten.
        var filter = allPorts ? "-t TCP" : $"-t TCP -p {WorldPort}";
        var log = Elevated(folder, "pktmon-start.log",
            $"pktmon stop & pktmon filter remove & pktmon filter add ForeverCapture {filter} & pktmon start --capture --comp nics --pkt-size 0 --file-size 16384 --file-name \"{etl}\"");
        if (!File.Exists(etl)) throw new InvalidOperationException("The packet monitor did not start. Its output:\n" + log);
    }

    // Stops the recording and converts it; returns the pcapng path.
    public static string Stop(string folder)
    {
        string etl = Path.Combine(folder, "net.etl"), pcap = Path.Combine(folder, "net.pcapng");
        var log = Elevated(folder, "pktmon-stop.log", $"pktmon stop & pktmon filter remove & pktmon etl2pcap \"{etl}\" --out \"{pcap}\"");
        if (!File.Exists(pcap)) throw new InvalidOperationException("The packet monitor recording could not be converted. Its output:\n" + log);
        return pcap;
    }

    public static void StopOnly(string folder) => Elevated(folder, "pktmon-stop.log", "pktmon stop & pktmon filter remove");

    private static string Elevated(string folder, string logName, string commands)
    {
        var log = Path.Combine(folder, logName);
        try
        {
            using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"({commands}) > \"{log}\" 2>&1\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })
                ?? throw new InvalidOperationException("The packet monitor command did not start.");
            process.WaitForExit();
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The Windows administrator prompt was declined; the packet monitor needs it.");
        }
        return File.Exists(log) ? File.ReadAllText(log) : "";
    }
}

internal static class WorldCapture
{
    private const string Magic = "FWCAP1";
    private static readonly byte[] Greeting = Encoding.ASCII.GetBytes("WORLD OF WARCRAFT CONNECTION - ");
    // What the game sends is small (its movement, a few hundred bytes a second); the cap only guards against a runaway stream.
    private const int ClientKeep = 64 << 20, MaxPending = 32 << 20;

    private sealed class Half
    {
        public uint Next;                 // next expected sequence number
        public bool Started, Lost;
        public MemoryStream Data = new();
        public SortedDictionary<uint, byte[]> Pending = new();
        public int PendingBytes;
        public bool IsServer;
    }
    private sealed class Flow { public Half Client = new(), Server = new() { IsServer = true }; public string ServerName; public bool Rejected; }

    /// <summary>The world connections of a stream of TCP packets, fed one by one (from a file, or live); safe across threads.</summary>
    internal sealed class TcpStreams
    {
        private readonly Dictionary<string, Flow> flows = new();
        private readonly List<Flow> opened = new();

        public void Add(IPAddress source, int sourcePort, IPAddress target, int targetPort, uint sequence, int flags, byte[] payload)
        {
            lock (flows)
            {
                string forward = $"{source}:{sourcePort}>{target}:{targetPort}", back = $"{target}:{targetPort}>{source}:{sourcePort}";
                bool syn = (flags & 0x02) != 0, ack = (flags & 0x10) != 0;
                if (syn && !ack)   // a new connection from this PC (also replaces an older one on the same ports)
                {
                    var flow = new Flow { ServerName = $"{target}:{targetPort}" };
                    flow.Client.Next = sequence + 1; flow.Client.Started = true;
                    flows[forward] = flow; opened.Add(flow);
                    return;
                }
                Half half;
                if (flows.TryGetValue(forward, out var mine)) half = mine.Client;
                else if (flows.TryGetValue(back, out mine)) half = mine.Server;
                else return;   // opened before the recording began: its key exchange is missing anyway
                if (mine.Rejected) return;
                if (syn) { half.Next = sequence + 1; half.Started = true; return; }
                if (!half.Started || half.Lost || payload.Length == 0) return;
                Deliver(half, sequence, payload);
                // Anything that does not open with the world greeting is another program's traffic: forget it.
                if (half.Data.Length >= Greeting.Length && !half.Data.GetBuffer().AsSpan(0, Greeting.Length).SequenceEqual(Greeting))
                { mine.Rejected = true; mine.Client = mine.Server = null; }
            }
        }

        /// <summary>The world connections so far, in the order they were opened.</summary>
        public List<WorldConnection> Connections()
        {
            lock (flows)
                return opened.Where(f => !f.Rejected && f.Server.Data.Length > Greeting.Length)
                    .Select(f => new WorldConnection(f.ServerName, f.Client.Data.ToArray(), f.Server.Data.ToArray(), !f.Server.Lost && f.Server.Pending.Count == 0)).ToList();
        }
    }

    // Reads a pcapng file and returns the world connections in the order they were opened.
    public static List<WorldConnection> Extract(string pcapPath)
    {
        var streams = new TcpStreams();
        PcapNg.ReadTcp(pcapPath, streams.Add);
        return streams.Connections();
    }

    // In-order reassembly; retransmissions and overlaps are dropped, out-of-order segments wait in Pending.
    // shortcut: sequence numbers are compared within one 2 GB window, enough for a play session; widen to 64-bit offsets if a
    // single connection ever carries more.
    private static void Deliver(Half half, uint sequence, byte[] payload)
    {
        var ahead = unchecked((int)(sequence - half.Next));
        if (ahead > 0)
        {
            if (half.Pending.TryGetValue(sequence, out var old) && old.Length >= payload.Length) return;
            half.PendingBytes += payload.Length - (old?.Length ?? 0);
            half.Pending[sequence] = payload;
            if (half.PendingBytes > MaxPending) { half.Lost = true; half.Pending.Clear(); }   // a packet never arrived in the recording
            return;
        }
        Append(half, payload, -ahead);
        while (half.Pending.Count > 0)
        {
            var (next, bytes) = half.Pending.MinBy(p => unchecked((int)(p.Key - half.Next)));
            var gap = unchecked((int)(next - half.Next));
            if (gap > 0) break;
            half.Pending.Remove(next); half.PendingBytes -= bytes.Length;
            Append(half, bytes, -gap);
        }
    }

    private static void Append(Half half, byte[] payload, int skip)
    {
        if (skip >= payload.Length) return;
        half.Next += (uint)(payload.Length - skip);
        // From the game: its first packet holds the local challenge, the rest its movement (the character's path).
        if (half.IsServer || half.Data.Length < ClientKeep) half.Data.Write(payload, skip, payload.Length - skip);
    }

    public static void Write(string path, List<WorldConnection> connections)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Magic); writer.Write(connections.Count);
        foreach (var c in connections)
        {
            writer.Write(c.Server); writer.Write(c.Complete);
            writer.Write(c.ClientBytes.Length); writer.Write(c.ClientBytes);
            writer.Write(c.ServerBytes.Length); writer.Write(c.ServerBytes);
        }
    }

    public static List<WorldConnection> Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.ReadString() != Magic) throw new InvalidDataException("Not a world capture file: " + path);
        var list = new List<WorldConnection>();
        for (var count = reader.ReadInt32(); count > 0; count--)
        {
            var server = reader.ReadString(); var complete = reader.ReadBoolean();
            var client = reader.ReadBytes(reader.ReadInt32());
            list.Add(new WorldConnection(server, client, reader.ReadBytes(reader.ReadInt32()), complete));
        }
        return list;
    }
}

// Just enough pcapng: little-endian files, Ethernet or raw-IP interfaces, IPv4 and IPv6 without extension headers.
internal static class PcapNg
{
    public delegate void TcpSegment(IPAddress source, int sourcePort, IPAddress target, int targetPort, uint sequence, int flags, byte[] payload);

    public static void ReadTcp(string path, TcpSegment segment)
    {
        using var file = new BufferedStream(File.OpenRead(path), 1 << 20);
        var linkTypes = new List<int>();
        var head = new byte[8];
        while (ReadExact(file, head, 8))
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(head);
            var length = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4));
            if (length < 12 || length > 1 << 26 || length % 4 != 0) throw new InvalidDataException("Damaged or big-endian pcapng file: " + path);
            var body = new byte[length - 8];
            if (!ReadExact(file, body, body.Length)) break;   // recording cut short: use what is there
            if (type == 0x0A0D0D0A)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(body) != 0x1A2B3C4D) throw new InvalidDataException("Big-endian pcapng files are not supported.");
                linkTypes.Clear();
            }
            else if (type == 1) linkTypes.Add(BinaryPrimitives.ReadUInt16LittleEndian(body));
            else if (type == 6 && body.Length >= 24)   // enhanced packet: interface, timestamp (2), captured length, original length, data
            {
                var nic = BinaryPrimitives.ReadInt32LittleEndian(body);
                var captured = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(12));
                if (nic < 0 || nic >= linkTypes.Count || captured < 0 || 20 + captured > body.Length) continue;
                Packet(body.AsSpan(20, captured), linkTypes[nic], segment);
            }
        }
    }

    public static void Packet(ReadOnlySpan<byte> frame, int linkType, TcpSegment segment)
    {
        if (linkType == 1)   // Ethernet, with optional VLAN tags
        {
            if (frame.Length < 14) return;
            var at = 12;
            while (at + 4 <= frame.Length && (frame[at] == 0x81 && frame[at + 1] == 0x00 || frame[at] == 0x88 && frame[at + 1] == 0xA8)) at += 4;
            if (at + 2 > frame.Length) return;
            var etherType = frame[at] << 8 | frame[at + 1];
            if (etherType != 0x0800 && etherType != 0x86DD) return;
            frame = frame[(at + 2)..];
        }
        else if (linkType is not (101 or 228 or 229)) return;
        if (frame.Length < 20) return;
        IPAddress source, target; ReadOnlySpan<byte> tcp;
        if (frame[0] >> 4 == 4)
        {
            int headerLength = (frame[0] & 15) * 4, total = frame[2] << 8 | frame[3];
            // A zero total length means the adapter fills it in later (large send): take the rest of the frame.
            if (total == 0) total = frame.Length;
            if (frame[9] != 6 || headerLength < 20 || total < headerLength || total > frame.Length) return;
            if (((frame[6] & 0x3F) << 8 | frame[7]) != 0) return;   // IP fragment
            source = new IPAddress(frame.Slice(12, 4)); target = new IPAddress(frame.Slice(16, 4));
            tcp = frame[headerLength..total];
        }
        else if (frame[0] >> 4 == 6)
        {
            if (frame.Length < 40 || frame[6] != 6) return;
            var payloadLength = frame[4] << 8 | frame[5];
            if (payloadLength == 0) payloadLength = frame.Length - 40;
            if (40 + payloadLength > frame.Length) return;
            source = new IPAddress(frame.Slice(8, 16)); target = new IPAddress(frame.Slice(24, 16));
            tcp = frame.Slice(40, payloadLength);
        }
        else return;
        if (tcp.Length < 20) return;
        var dataOffset = (tcp[12] >> 4) * 4;
        if (dataOffset < 20 || dataOffset > tcp.Length) return;
        segment(source, tcp[0] << 8 | tcp[1], target, tcp[2] << 8 | tcp[3], BinaryPrimitives.ReadUInt32BigEndian(tcp[4..]), tcp[13], tcp[dataOffset..].ToArray());
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        for (var done = 0; done < count;)
        {
            var read = stream.Read(buffer, done, count - done);
            if (read == 0) return false;
            done += read;
        }
        return true;
    }
}
