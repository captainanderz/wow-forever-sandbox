// Live recording through Npcap (https://npcap.com), when it is installed and open to every user (its "admin only" option
// off): the world port is recorded straight into net.pcapng and handed to the decoder as it arrives, with no administrator
// prompt. Without Npcap the recording falls back to Windows' packet monitor (PacketMonitor), which has no live feed.
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace ForeverCapture;

internal sealed class LiveCapture : IDisposable
{
    private readonly List<(IntPtr Handle, Thread Thread)> adapters = new();
    private readonly PcapNgWriter writer;
    private readonly PcapNg.TcpSegment segment;
    private volatile bool stopping;
    public int AdapterCount => adapters.Count;
    public long Packets;

    private LiveCapture(string pcapPath, PcapNg.TcpSegment onSegment) { writer = new PcapNgWriter(pcapPath); segment = onSegment; }

    /// <summary>Records every adapter's TCP packets that match <paramref name="filter"/>; null when Npcap is missing or opens no adapter.</summary>
    public static LiveCapture TryStart(string pcapPath, string filter, PcapNg.TcpSegment onSegment, out string reason)
    {
        reason = null;
        var folder = Path.Combine(Environment.SystemDirectory, "Npcap");
        if (!File.Exists(Path.Combine(folder, "wpcap.dll"))) { reason = "Npcap is not installed"; return null; }
        // Npcap installs its DLLs in its own folder unless it was installed in WinPcap compatible mode.
        Native.SetDllDirectory(folder);
        var errors = new StringBuilder(256);
        if (Native.pcap_findalldevs(out var list, errors) != 0) { reason = "Npcap: " + errors; return null; }
        var capture = new LiveCapture(pcapPath, onSegment);
        try
        {
            for (var device = list; device != IntPtr.Zero; device = Marshal.ReadIntPtr(device))
            {
                var name = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(device, IntPtr.Size));
                var handle = Native.pcap_open_live(name, 65535, 0, 250, errors);
                if (handle == IntPtr.Zero) continue;   // an adapter this user may not open (or one that is down)
                var program = new BpfProgram();
                if (Native.pcap_compile(handle, ref program, filter, 1, 0xFFFFFFFF) != 0 || Native.pcap_setfilter(handle, ref program) != 0)
                { Native.pcap_close(handle); continue; }
                Native.pcap_freecode(ref program);
                var linkType = Native.pcap_datalink(handle);
                var index = capture.writer.AddInterface(linkType);
                var thread = new Thread(() => capture.Run(handle, linkType, index)) { IsBackground = true, Name = "capture " + index };
                capture.adapters.Add((handle, thread));
            }
        }
        finally { Native.pcap_freealldevs(list); }
        if (capture.adapters.Count == 0)
        {
            capture.writer.Dispose();
            reason = Native.AdminOnly() ? "Npcap is set to administrators only" : "Npcap opened no network adapter";
            return null;
        }
        foreach (var (_, thread) in capture.adapters) thread.Start();
        return capture;
    }

    private void Run(IntPtr handle, int linkType, int index)
    {
        while (!stopping)
        {
            var result = Native.pcap_next_ex(handle, out var header, out var data);
            if (result == 0) continue;     // read timeout: check whether to stop
            if (result < 0) return;        // the adapter went away
            // pcap_pkthdr: two 32-bit timeval fields, then the captured and the original length.
            long seconds = (uint)Marshal.ReadInt32(header), micros = (uint)Marshal.ReadInt32(header, 4);
            var length = Marshal.ReadInt32(header, 8);
            var frame = new byte[length];
            Marshal.Copy(data, frame, 0, length);
            writer.Write(index, seconds * 1_000_000 + micros, frame, Marshal.ReadInt32(header, 12));
            Interlocked.Increment(ref Packets);
            PcapNg.Packet(frame, linkType, segment);
        }
    }

    public void Dispose()
    {
        stopping = true;
        foreach (var (_, thread) in adapters) thread.Join(2000);
        foreach (var (handle, _) in adapters) Native.pcap_close(handle);
        adapters.Clear();
        writer.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BpfProgram { public uint Length; public IntPtr Instructions; }

    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool SetDllDirectory(string path);
        [DllImport("wpcap.dll", CharSet = CharSet.Ansi)] public static extern int pcap_findalldevs(out IntPtr list, StringBuilder errors);
        [DllImport("wpcap.dll")] public static extern void pcap_freealldevs(IntPtr list);
        [DllImport("wpcap.dll", CharSet = CharSet.Ansi)] public static extern IntPtr pcap_open_live(string device, int snapshotLength, int promiscuous, int timeoutMs, StringBuilder errors);
        [DllImport("wpcap.dll", CharSet = CharSet.Ansi)] public static extern int pcap_compile(IntPtr handle, ref BpfProgram program, string filter, int optimize, uint netmask);
        [DllImport("wpcap.dll")] public static extern int pcap_setfilter(IntPtr handle, ref BpfProgram program);
        [DllImport("wpcap.dll")] public static extern void pcap_freecode(ref BpfProgram program);
        [DllImport("wpcap.dll")] public static extern int pcap_datalink(IntPtr handle);
        [DllImport("wpcap.dll")] public static extern int pcap_next_ex(IntPtr handle, out IntPtr header, out IntPtr data);
        [DllImport("wpcap.dll")] public static extern void pcap_close(IntPtr handle);

        public static bool AdminOnly()
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\npcap\Parameters");
            return key?.GetValue("AdminOnly") is int value && value != 0;
        }
    }
}

/// <summary>A pcapng file written as packets arrive (little-endian, microsecond timestamps), flushed every second.</summary>
internal sealed class PcapNgWriter : IDisposable
{
    private readonly FileStream file;
    private int interfaces;
    private DateTime flushed = DateTime.UtcNow;

    public PcapNgWriter(string path)
    {
        file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        // Section header: magic, version 1.0, unknown section length.
        Block(0x0A0D0D0A, w => { w.Write(0x1A2B3C4Du); w.Write((ushort)1); w.Write((ushort)0); w.Write(-1L); });
    }

    public int AddInterface(int linkType)
    {
        lock (file) { Block(1, w => { w.Write((ushort)linkType); w.Write((ushort)0); w.Write(0); }); return interfaces++; }
    }

    public void Write(int index, long microseconds, byte[] frame, int originalLength)
    {
        lock (file)
        {
            Block(6, w =>
            {
                w.Write(index); w.Write((uint)(microseconds >> 32)); w.Write((uint)microseconds);
                w.Write(frame.Length); w.Write(originalLength); w.Write(frame); w.Write(new byte[(4 - frame.Length % 4) % 4]);
            });
            if (DateTime.UtcNow - flushed > TimeSpan.FromSeconds(1)) { file.Flush(); flushed = DateTime.UtcNow; }
        }
    }

    // A block: type, total length, body, total length again.
    private void Block(uint type, Action<BinaryWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, Encoding.ASCII, true)) body(w);
        var length = (int)buffer.Length + 12;
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, type);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), length);
        buffer.ToArray().CopyTo(bytes, 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(length - 4), length);
        file.Write(bytes);
    }

    public void Dispose() { lock (file) file.Dispose(); }
}
