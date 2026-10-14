// Realm-key write (opt-in): the beta keeps the Ed25519 public keys that authenticate EnterEncryptedMode in a 480-byte heap table;
// the sandbox's world server signs with the key whose public half replaces group 8, as ForeverLauncher does. Rules: only
// committed MEM_PRIVATE/PAGE_READWRITE memory of the client this launcher started, a fully validated table, one 32-byte write while the client is paused, read-back,
// and a guarded rollback. Nothing on disk changes; it is gone when the game closes.
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ForeverSandbox;

internal enum StoreState { Invalid, Original, Patched }

internal sealed class RealmKeyStore
{
    private const int EntrySize = 40, EntryCount = 12, KeySize = 32, BlockSize = EntrySize * EntryCount, TargetGroup = 8;
    private const int KeyOffset = (TargetGroup - 1) * EntrySize + 4;
    private const uint MemCommit = 0x1000, MemPrivate = 0x20000, PageReadWrite = 4;
    private static readonly byte[] Flags = { 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1 };
    private static readonly byte[] Padding = { 0x7F, 0x00, 0x00 };
    private static readonly byte[][] KnownKeys = new[]
    {
        "9B0671C815DFF513BFD4A2B26AE1F84EC9106841B2FB620DB65F6ADE7C21AD06", "112451B9843C2799E4750AA3D9B9AFABF536A645B863C4ADB2078B932B354E04",
        "A6B858485748BF38BE193517AB90F8DE169EFF0989EA9360DB346A378B0FFE15", "50FFDEB807F8FF73A299A16000AA6575C5945BE875ADFC8795374ED3415249AC",
        "9F842D078755647C008E5FE3E12B839A981DE02A1F520CB2545CC04CD2323E0E", "B8CC75864D8EF461D8DD6AA7425E2C63C24D3982066B8D773A1549BFE24E05EE",
        "76BB7BBDD9F34E124A4573C3AA227E3C87CC603506697D054F6FDEC342E56EBF", "1FD6DD8FA0EC30D39E3F72E755B8A045BDE0F70449DC71008B767C2EAA89FB9F",
        "15D618BD7DB577BD9A8D45769C59E4FC631633BF447398A4B489B4C26FBC03AD", "B34EC592D2AC4993F5FFC85B15C3DA9078694051CB224345592AF7136D796C99",
        "9E91586DD4B115AB0568B21959E181F6545910BC5E2F30B8975CA09D7BC3EFCE", "769C824A1DADD19AEEFA420414D1732DAF44537F98AA229FC5477BCA55F9D59A",
    }.Select(Convert.FromHexString).ToArray();
    // The sandbox world server's signing key (public half), as ForeverLauncher's group-8 default.
    public static readonly byte[] SandboxPublicKey = Convert.FromHexString("02596F0D0C061A8B30745988FD72C59E29EC367FB0F341F28E0F08D037BAFC69");

    private readonly IntPtr process;
    public RealmKeyStore(Process owned) { process = owned.Handle; }

    public readonly record struct Region(ulong Base, ulong Size, uint State, uint Protect, uint Type)
    {
        public ulong End => Base + Size;
        public bool PrivateReadWrite => State == MemCommit && Type == MemPrivate && Protect == PageReadWrite;
        public bool Contains(ulong address, ulong size) => Size > 0 && size > 0 && Base <= address && address + size <= End;
    }

    public static StoreState Validate(ReadOnlySpan<byte> block)
    {
        if (block.Length != BlockSize) return StoreState.Invalid;
        for (var i = 0; i < EntryCount; i++)
        {
            var entry = block.Slice(i * EntrySize, EntrySize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(entry) != i + 1) return StoreState.Invalid;
            var key = entry.Slice(4, KeySize);
            if (!key.SequenceEqual(KnownKeys[i]) && !(i == TargetGroup - 1 && key.SequenceEqual(SandboxPublicKey))) return StoreState.Invalid;
            if (entry[36] != Flags[i] || !entry.Slice(37, 3).SequenceEqual(Padding)) return StoreState.Invalid;
        }
        return block.Slice(KeyOffset, KeySize).SequenceEqual(SandboxPublicKey) ? StoreState.Patched : StoreState.Original;
    }

    private Region? Query(ulong address)
    {
        if (Native.VirtualQueryEx(process, (IntPtr)(long)address, out var info, (IntPtr)Marshal.SizeOf<Native.MemoryBasicInformation>()) == IntPtr.Zero) return null;
        return new Region((ulong)info.BaseAddress, (ulong)info.RegionSize, info.State, info.Protect, info.Type);
    }

    // Reads only inside a freshly queried private read/write region.
    private byte[] Read(ulong address, int size)
    {
        if (Query(address) is not { PrivateReadWrite: true } region || !region.Contains(address, (ulong)size)) return null;
        var buffer = new byte[size];
        return Native.ReadProcessMemory(process, (IntPtr)(long)address, buffer, (IntPtr)size, out var read) && (long)read == size ? buffer : null;
    }

    private bool WriteKey(ulong address, byte[] value)
    {
        if (Query(address) is not { PrivateReadWrite: true } region || !region.Contains(address, KeySize)) return false;
        return Native.WriteProcessMemory(process, (IntPtr)(long)address, value, (IntPtr)value.Length, out var written) && (long)written == value.Length;
    }

    public StoreState Inspect(ulong address) => Read(address, BlockSize) is { } block ? Validate(block) : StoreState.Invalid;

    // Bounded sweep of the client's private read/write memory for the table (anchor: the first known key, 4-byte aligned).
    public List<ulong> Scan(TimeSpan budget, long maxBytes = 4L << 30, int maxQueries = 100000, int maxCandidates = 256, int maxStores = 16)
    {
        var clock = Stopwatch.StartNew();
        var regions = new List<Region>();
        for (ulong address = 0; address < 0x7FFFFFFFFFFF && regions.Count < 32768; )
        {
            if (maxQueries-- <= 0 || clock.Elapsed > budget) break;
            if (Query(address) is not { } region || region.Size == 0 || region.End <= address) break;
            address = region.End;
            if (region.PrivateReadWrite) regions.Add(region);
        }
        regions.Sort((a, b) => a.Size != b.Size ? a.Size.CompareTo(b.Size) : a.Base.CompareTo(b.Base));
        var stores = new List<ulong>(); var seen = new HashSet<ulong>();
        long requested = 0; var candidates = 0;
        var anchor = KnownKeys[0];
        foreach (var region in regions)
        {
            byte[] carry = Array.Empty<byte>();
            for (ulong offset = 0; offset < region.Size; )
            {
                if (clock.Elapsed > budget || stores.Count >= maxStores || candidates >= maxCandidates) return stores;
                var size = (int)Math.Min(1UL << 20, region.Size - offset);
                if (requested + size > maxBytes) return stores;
                var address = region.Base + offset; requested += size;
                var chunk = Read(address, size);
                if (chunk == null) { offset += (ulong)size; carry = Array.Empty<byte>(); continue; }
                var window = carry.Length == 0 ? chunk : carry.Concat(chunk).ToArray();
                var windowBase = address - (ulong)carry.Length;
                for (var position = window.AsSpan().IndexOf(anchor); position >= 0; )
                {
                    var anchorAddress = windowBase + (ulong)position; var candidate = anchorAddress - 4;
                    if (anchorAddress % 4 == 0 && seen.Add(candidate) && region.Contains(candidate, BlockSize))
                    {
                        candidates++; requested += BlockSize;
                        if (Inspect(candidate) != StoreState.Invalid) stores.Add(candidate);
                    }
                    var next = window.AsSpan(position + 1).IndexOf(anchor);
                    position = next < 0 ? -1 : position + 1 + next;
                }
                carry = window[^(KeySize - 1)..];
                offset += (ulong)size;
            }
        }
        return stores;
    }

    // One guarded 32-byte transaction while the client is paused: unchanged table, write, read-back, rollback on failure.
    public string Patch(ulong address)
    {
        var before = Read(address, BlockSize);
        var state = before == null ? StoreState.Invalid : Validate(before);
        if (state != StoreState.Original) return state == StoreState.Patched ? "already_patched" : "invalid";
        if (Native.NtSuspendProcess(process) != 0) throw new InvalidOperationException("Cannot pause the game for the realm-key write.");
        try
        {
            var current = Read(address, BlockSize);
            if (current == null || !current.AsSpan().SequenceEqual(before)) return "store_changed_before_write";
            var wrote = WriteKey(address + KeyOffset, SandboxPublicKey);
            var expected = before.ToArray(); SandboxPublicKey.CopyTo(expected, KeyOffset);
            if (wrote && Read(address, BlockSize) is { } after && after.AsSpan().SequenceEqual(expected)) return "patched";
            // Restore only our own 32 bytes, and only while the rest of the table is unchanged.
            var now = Read(address, BlockSize);
            if (now != null && now.AsSpan(0, KeyOffset).SequenceEqual(before.AsSpan(0, KeyOffset)) && now.AsSpan(KeyOffset + KeySize).SequenceEqual(before.AsSpan(KeyOffset + KeySize))
                && WriteKey(address + KeyOffset, before[KeyOffset..(KeyOffset + KeySize)]) && Read(address, BlockSize) is { } restored && restored.AsSpan().SequenceEqual(before))
                return "failed_restored";
            throw new InvalidOperationException("The realm-key write failed and could not be undone; the game must be closed.");
        }
        finally
        {
            if (Native.NtResumeProcess(process) != 0) throw new InvalidOperationException("Cannot resume the game after the realm-key write.");
        }
    }
}

// Schedules scans around login and world connections (server diagnostics), and keeps the "realm-key-patched" signal the server
// waits for before EnterEncryptedMode: present only while every found table holds the sandbox key.
internal sealed class RealmKeyMonitor
{
    private static readonly string[] Triggers = { "\"bgs_v2_logon_complete\"", "\"world_initializer\"", "\"realm_join\"", "\"world_log_disconnect\"" };
    private readonly RealmKeyStore store;
    private readonly string diagnostics, signal;
    private long offset;
    private DateTime nextScan = DateTime.UtcNow.AddSeconds(10), nextFull = DateTime.MinValue, activeUntil = DateTime.MinValue;
    private List<ulong> known = new();
    private string lastSummary;

    public RealmKeyMonitor(Process client, string diagnosticLog, string signalFile)
    {
        store = new RealmKeyStore(client); diagnostics = diagnosticLog; signal = signalFile;
    }

    private bool Triggered()
    {
        try
        {
            using var log = new FileStream(diagnostics, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            log.Position = offset;
            var buffer = new byte[65536]; var read = log.Read(buffer);
            var complete = Array.LastIndexOf(buffer, (byte)'\n', Math.Max(read - 1, 0)) + 1;
            if (read == 0) complete = 0;
            offset += complete;
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, complete);
            return Triggers.Any(text.Contains);
        }
        catch (IOException) { return false; }
    }

    // Returns a status line when something changed.
    public string Poll()
    {
        var now = DateTime.UtcNow;
        if (Triggered()) { activeUntil = now.AddSeconds(20); nextScan = now; nextFull = now; }
        if (now < nextScan) return null;
        var full = now >= nextFull || known.Count == 0;
        var found = known.Where(a => store.Inspect(a) != StoreState.Invalid).ToList();
        if (full) foreach (var address in store.Scan(TimeSpan.FromSeconds(8))) if (!found.Contains(address)) found.Add(address);
        var writes = found.Where(a => store.Inspect(a) == StoreState.Original).Select(store.Patch).ToList();
        var states = found.Select(store.Inspect).ToList();
        known = found.Where((_, i) => states[i] != StoreState.Invalid).ToList();
        var ready = known.Count > 0 && states.All(s => s == StoreState.Patched);
        try { if (ready && !File.Exists(signal)) File.WriteAllText(signal, "patched\n"); else if (!ready && File.Exists(signal)) File.Delete(signal); } catch (IOException) { }
        var active = DateTime.UtcNow < activeUntil;
        nextScan = DateTime.UtcNow.AddSeconds(active ? 0.5 : known.Count > 0 ? 5 : 30);
        if (full) nextFull = DateTime.UtcNow.AddSeconds(active ? (known.Count == 0 ? 0 : 2) : 30);
        var summary = $"{known.Count} key table(s): {string.Join(", ", states.Select(s => s.ToString().ToLowerInvariant()))}" + (writes.Count > 0 ? $"; {string.Join(", ", writes)}" : "");
        if (summary == lastSummary) return null;
        lastSummary = summary;
        return summary;
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, IntPtr length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr process);
}
