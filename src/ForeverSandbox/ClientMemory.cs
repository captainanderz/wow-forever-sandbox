// Access to the running 70338 client this launcher started, shared by FogMonitor and WallOverlay (one instance per user):
// its image, committed readable regions, the main world scene, and guarded writes to private read/write data.
// The pinned WowB.exe layout is fixed (the launcher checks its hash before starting it); see docs/fog.md.
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ForeverSandbox;

internal sealed class ClientMemory
{
    // Image size, .data range and RVAs in .data.
    private const uint ImageSize = 0x84B8000, DataStart = 0x5664000, DataEnd = 0x5664000 + 0x2A1C5E1;
    private const uint SceneGlobal = 0x7A08CF8, SceneListLinkOffset = 0x5746908, SceneListHead = 0x5746910;
    // The client's image is a mapped section (MEM_MAPPED), not a loader image (MEM_IMAGE).
    private const uint MemCommit = 0x1000, MemPrivate = 0x20000, MemMapped = 0x40000, MemImage = 0x1000000, PageReadWrite = 4;

    private readonly Process client;
    private readonly IntPtr process;
    public ulong Image { get; private set; }
    // The running image is not the expected layout: nothing is read or written.
    public bool Unsupported { get; private set; }

    public ClientMemory(Process owned) { client = owned; process = owned.Handle; }

    public bool Attach()
    {
        if (Image != 0) return true;
        if (Unsupported) return false;
        try { Image = (ulong)(long)client.MainModule.BaseAddress; }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or NullReferenceException) { return false; }
        if (Read(Image, 0x400, imageOnly: true) is not { } headers) { Image = 0; return false; } // not mapped yet: retry
        if (headers[0] == 'M' && headers[1] == 'Z' && BinaryPrimitives.ReadInt32LittleEndian(headers.AsSpan(0x3C)) is var pe
            && pe is > 0 and < 0x3A0 && U32(headers, pe + 24 + 56) == ImageSize) return true;
        Image = 0; Unsupported = true;
        return false;
    }

    // The main world scene, only while it is linked in the client's scene list (a freed scene is unlinked).
    public ulong MainScene()
    {
        if (Data(SceneGlobal, 8) is not { } pointer || Data(SceneListLinkOffset, 8) is not { } link || BinaryPrimitives.ReadUInt64LittleEndian(link) != 0) return 0;
        var scene = BinaryPrimitives.ReadUInt64LittleEndian(pointer);
        if (scene == 0 || scene % 8 != 0) return 0;
        var node = Q(Image + SceneListHead + 8);
        for (var i = 0; i < 64 && node is { } n && n != 0 && (n & 1) == 0; i++)
        {
            if (n == scene) return scene;
            node = Q(n + 8);
        }
        return 0;
    }

    // The scene's current map ID (scene +0x10 -> world, +0x18), or null outside the world.
    public uint? Map(ulong scene) => Q(scene + 0x10) is { } world && world != 0 && U32(world + 0x18) is { } map && map != uint.MaxValue ? map : null;

    public bool Paused(Action action)
    {
        if (Native.NtSuspendProcess(process) != 0) return false;
        try { action(); return true; }
        finally { if (Native.NtResumeProcess(process) != 0) throw new InvalidOperationException("Cannot resume the game after a memory change."); }
    }

    private Native.MemoryBasicInformation? Query(ulong address) =>
        Native.VirtualQueryEx(process, (IntPtr)(long)address, out var info, (IntPtr)Marshal.SizeOf<Native.MemoryBasicInformation>()) == IntPtr.Zero ? null : info;

    private static bool Inside(Native.MemoryBasicInformation r, ulong address, int size) =>
        (ulong)r.BaseAddress <= address && address + (ulong)size <= (ulong)r.BaseAddress + (ulong)r.RegionSize;

    // Reads only inside one committed readable region (not no-access or guard); image reads only inside this client's image.
    public byte[] Read(ulong address, int size, bool imageOnly = false)
    {
        if (size <= 0 || Query(address) is not { } r || r.State != MemCommit || r.Protect == 0 || (r.Protect & 0x101) != 0 || !Inside(r, address, size)) return null;
        if (imageOnly && ((ulong)r.AllocationBase != Image || r.Type is not (MemImage or MemMapped))) return null;
        var buffer = new byte[size];
        return Native.ReadProcessMemory(process, (IntPtr)(long)address, buffer, (IntPtr)size, out var read) && (long)read == size ? buffer : null;
    }

    public byte[] Data(uint rva, int size) => rva >= DataStart && rva + (uint)size <= DataEnd ? Read(Image + rva, size, imageOnly: true) : null;

    // Writes only inside committed private read/write memory, then reads back.
    public bool Write(ulong address, byte[] value)
    {
        if (Query(address) is not { } r || r.State != MemCommit || r.Type != MemPrivate || r.Protect != PageReadWrite || !Inside(r, address, value.Length)) return false;
        return Native.WriteProcessMemory(process, (IntPtr)(long)address, value, (IntPtr)value.Length, out var written) && (long)written == value.Length
            && Read(address, value.Length) is { } back && back.AsSpan().SequenceEqual(value);
    }

    public ulong? Q(ulong address) => Read(address, 8) is { } b ? BinaryPrimitives.ReadUInt64LittleEndian(b) : null;
    public uint? U32(ulong address) => Read(address, 4) is { } b ? BinaryPrimitives.ReadUInt32LittleEndian(b) : null;
    public static ulong U64(byte[] b, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(offset));
    public static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));
}
