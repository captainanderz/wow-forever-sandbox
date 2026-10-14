// .walls overlay: draws the sandbox server's impassable terrain walls (impass-walls.json, ForeverImpass.cs) over the game window
// in 3D, with the game's own camera: the render view's view and projection matrices are read from the running client every
// frame. Nothing in the game is written. A click-through, topmost, transparent window covers the game's client area while the
// game is in front; Direct3D 11 renders into a DirectComposition swap chain with premultiplied alpha.
// The look follows the wowmap 3D viewer: faint wall faces from the chunk floor to the world ceiling, a floor line and two corner
// guides per edge; yellow next to walkable ground, red next to impassable or missing ground; drawn through everything.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ForeverSandbox;

internal sealed unsafe class WallOverlay : IDisposable
{
    // Main scene +0x78 -> render view: matrices from +0x530 (row-major, row vectors, reversed Z; see Camera).
    private const uint SceneView = 0x78, ViewMatrices = 0x530;
    private const float WorldCeiling = 10000f, OutlineLift = 0.12f;
    // Draw order and RGBA (wowmap): red faces, yellow faces, red outline, yellow outline.
    private static readonly float[][] Colours = { new[] { 1f, 0.20f, 0.14f, 0.069f }, new[] { 1f, 0.85f, 0.20f, 0.059f }, new[] { 1f, 0.05f, 0.04f, 0.96f }, new[] { 1f, 0.88f, 0.02f, 0.96f } };
    private const string Shader = @"cbuffer C : register(b0) { row_major float4x4 viewProj; float4 colour; };
float4 vs(float3 p : POSITION) : SV_Position { return mul(float4(p, 1), viewProj); }
float4 ps() : SV_Target { return float4(colour.rgb * colour.a, colour.a); }";

    private readonly Process client;
    private readonly string wallsFile;
    private readonly Thread thread;
    private volatile bool running = true;

    private DateTime wallsStamp;
    private uint wallsMap;
    private bool wallsOn;
    private readonly (int Start, int Count)[] ranges = new (int, int)[4];

    private IntPtr hwnd, device, context, swap, rtv, dcomp, target, visual, vertexShader, pixelShader, layout, constants, blend, raster, vertices;
    private int width, height;
    private bool shown;

    public WallOverlay(Process owned, string impassWallsFile)
    {
        client = owned; wallsFile = impassWallsFile;
        thread = new Thread(Run) { IsBackground = true, Name = "Walls overlay" };
        thread.Start();
    }

    public void Dispose() { running = false; thread.Join(2000); }

    private void Run()
    {
        // Broad catch: an overlay failure must never take down the launcher, which owns the game process.
        try { Loop(); }
        catch (Exception error) { Console.WriteLine("Walls: overlay stopped: " + error.Message); }
        finally { Cleanup(); }
    }

    private void Loop()
    {
        User32.SetThreadDpiAwarenessContext(new IntPtr(-4));   // physical pixels, like the game's client area
        var memory = new ClientMemory(client);
        var nextCheck = DateTime.MinValue; var game = IntPtr.Zero; var placed = (X: 0, Y: 0);
        while (running && !client.HasExited)
        {
            if (DateTime.UtcNow >= nextCheck)
            {
                nextCheck = DateTime.UtcNow.AddMilliseconds(250);
                LoadWalls();
                if (game == IntPtr.Zero || !User32.IsWindow(game)) game = GameWindow((uint)client.Id);
            }
            var camera = Visible(memory, game) ? Camera(memory) : null;
            User32.GetClientRect(game, out var rect); var origin = new User32.Point();
            if (camera == null || rect.Right <= 0 || rect.Bottom <= 0 || !User32.ClientToScreen(game, ref origin)) { Hide(); Pump(); Thread.Sleep(100); continue; }
            if (device == IntPtr.Zero) Create(rect.Right, rect.Bottom);
            if (rect.Right != width || rect.Bottom != height) { Resize(rect.Right, rect.Bottom); shown = false; }
            if (!shown || placed != (origin.X, origin.Y))
            {
                User32.SetWindowPos(hwnd, new IntPtr(-1), origin.X, origin.Y, width, height, 0x10 | 0x40);   // topmost, no activate, show
                shown = true; placed = (origin.X, origin.Y);
            }
            Render(camera);
            Pump();
        }
    }

    // Walls on, for the map the game shows, and the game window in front.
    private bool Visible(ClientMemory memory, IntPtr game)
    {
        if (!wallsOn || pending == null && vertices == IntPtr.Zero || game == IntPtr.Zero || User32.GetForegroundWindow() != game || User32.IsIconic(game)) return false;
        if (!memory.Attach()) return false;
        var scene = memory.MainScene();
        return scene != 0 && memory.Map(scene) == wallsMap;
    }

    // viewProj = view x projection, or null when the render view does not hold a sane camera.
    private static float[] Camera(ClientMemory memory)
    {
        var scene = memory.MainScene();
        // View matrix at +0x530, its rotation-only copy at +0x570, projection at +0x5B0.
        if (scene == 0 || memory.Q(scene + SceneView) is not { } view || view == 0 || memory.Read(view + ViewMatrices, 192) is not { } raw) return null;
        var m = new float[48];
        Buffer.BlockCopy(raw, 0, m, 0, 192);
        if (m.Any(v => !float.IsFinite(v))) return null;
        for (var r = 0; r < 3; r++)
        {
            var length = m[r * 4] * m[r * 4] + m[r * 4 + 1] * m[r * 4 + 1] + m[r * 4 + 2] * m[r * 4 + 2];
            if (MathF.Abs(length - 1) > 0.02f) return null;   // rotation rows
        }
        const int P = 32;
        if (m[P] <= 0.1f || m[P + 5] <= 0.1f || MathF.Abs(m[P + 11] - 1) > 0.001f || MathF.Abs(m[P + 15]) > 0.001f) return null;   // perspective
        var result = new float[16];
        for (var i = 0; i < 4; i++)
            for (var j = 0; j < 4; j++)
                for (var k = 0; k < 4; k++) result[i * 4 + j] += m[i * 4 + k] * m[P + k * 4 + j];
        return result;
    }

    private void LoadWalls()
    {
        DateTime stamp;
        try { stamp = File.Exists(wallsFile) ? File.GetLastWriteTimeUtc(wallsFile) : DateTime.MinValue; }
        catch (IOException) { return; }
        if (stamp == wallsStamp) return;
        wallsStamp = stamp;
        if (stamp == DateTime.MinValue) { wallsOn = false; return; }
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(wallsFile));
            var root = json.RootElement;
            var on = root.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean();
            if (!on) { if (wallsOn) Console.WriteLine("Walls: off."); wallsOn = false; return; }
            var map = root.GetProperty("map").GetUInt32();
            var groups = new List<float>[] { new(), new(), new(), new() };
            var count = 0;
            foreach (var e in root.GetProperty("edges").EnumerateArray())
            {
                float x1 = e[0].GetSingle(), y1 = e[1].GetSingle(), x2 = e[2].GetSingle(), y2 = e[3].GetSingle(), floor = e[4].GetSingle();
                var red = e[5].GetInt32() == 1; var top = MathF.Max(WorldCeiling, floor + 1); var lift = floor + OutlineLift;
                groups[red ? 0 : 1].AddRange(new[] { x1, y1, floor, x1, y1, top, x2, y2, floor, x2, y2, floor, x1, y1, top, x2, y2, top });
                groups[red ? 2 : 3].AddRange(new[] { x1, y1, lift, x2, y2, lift, x1, y1, lift, x1, y1, top, x2, y2, lift, x2, y2, top });
                count++;
            }
            wallsOn = true; wallsMap = map; pending = groups;
            Console.WriteLine($"Walls: {count} impassable edges around you (map {map}).");
        }
        // The server writes the file atomically; anything unreadable is retried on the next change.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or KeyNotFoundException or IndexOutOfRangeException)
        {
            wallsStamp = DateTime.MinValue;
        }
    }
    private List<float>[] pending;

    private void Render(float[] viewProj)
    {
        if (pending != null) { Upload(pending); pending = null; }
        var clear = stackalloc float[4];
        Call(context, 50, rtv, clear);                                          // ClearRenderTargetView
        if (vertices != IntPtr.Zero)
        {
            var targets = rtv; Call(context, 33, 1u, &targets, IntPtr.Zero);   // OMSetRenderTargets
            var viewport = stackalloc float[] { 0, 0, width, height, 0, 1 };
            Call(context, 44, 1u, viewport);                                    // RSSetViewports
            Call(context, 17, layout);                                          // IASetInputLayout
            var buffer = vertices; uint stride = 12, offset = 0;
            Call(context, 18, 0u, 1u, &buffer, &stride, &offset);               // IASetVertexBuffers
            Call(context, 11, vertexShader, IntPtr.Zero, 0u);                   // VSSetShader
            Call(context, 9, pixelShader, IntPtr.Zero, 0u);                     // PSSetShader
            var cb = constants;
            Call(context, 7, 0u, 1u, &cb); Call(context, 16, 0u, 1u, &cb);     // VS/PSSetConstantBuffers
            Call(context, 35, blend, (float*)null, 0xFFFFFFFFu);                // OMSetBlendState
            Call(context, 43, raster);                                          // RSSetState
            var data = stackalloc float[20];
            for (var i = 0; i < 16; i++) data[i] = viewProj[i];
            for (var g = 0; g < 4; g++)
            {
                if (ranges[g].Count == 0) continue;
                for (var i = 0; i < 4; i++) data[16 + i] = Colours[g][i];
                Call(context, 48, constants, 0u, null, data, 0u, 0u);           // UpdateSubresource
                Call(context, 24, g < 2 ? 4u : 2u);                             // IASetPrimitiveTopology: triangles / lines
                Call(context, 13, (uint)ranges[g].Count, (uint)ranges[g].Start); // Draw
            }
        }
        ((delegate* unmanaged<IntPtr, uint, uint, int>)Fn(swap, 8))(swap, 1, 0);   // Present (vsync)
    }

    private void Upload(List<float>[] groups)
    {
        Release(ref vertices);
        var all = new List<float>();
        for (var g = 0; g < 4; g++) { ranges[g] = (all.Count / 3, groups[g].Count / 3); all.AddRange(groups[g]); }
        if (all.Count == 0) return;
        var data = all.ToArray();
        fixed (float* p = data)
        {
            var desc = stackalloc uint[] { (uint)(data.Length * 4), 1, 1, 0, 0, 0 };   // immutable vertex buffer
            var init = stackalloc IntPtr[] { (IntPtr)p, IntPtr.Zero };
            IntPtr buffer;
            Check(((delegate* unmanaged<IntPtr, void*, void*, IntPtr*, int>)Fn(device, 3))(device, desc, init, &buffer), "CreateBuffer");
            vertices = buffer;
        }
    }

    private void Create(int w, int h)
    {
        // Window: popup, click-through (layered + transparent), topmost, never activated, no redirection bitmap (DirectComposition).
        var className = "ForeverSandboxWalls";
        fixed (char* name = className)
        {
            var wc = new User32.WndClassEx { Size = (uint)sizeof(User32.WndClassEx), WndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc, Instance = User32.GetModuleHandleW(null), ClassName = (IntPtr)name };
            User32.RegisterClassExW(ref wc);
            hwnd = User32.CreateWindowExW(0x8 | 0x80 | 0x20 | 0x80000 | 0x8000000 | 0x200000, className, "Forever Sandbox walls", 0x80000000, 0, 0, w, h, IntPtr.Zero, IntPtr.Zero, wc.Instance, IntPtr.Zero);
        }
        if (hwnd == IntPtr.Zero) throw new InvalidOperationException("Cannot create the overlay window.");
        User32.SetLayeredWindowAttributes(hwnd, 0, 255, 2);

        IntPtr d, c; int level;
        Check(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out d, out level, out c), "D3D11CreateDevice");
        device = d; context = c;
        var dxgi = Query(device, new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"));
        try
        {
            IntPtr adapter, factory, chain;
            Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(dxgi, 7))(dxgi, &adapter), "GetAdapter");
            var factoryId = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
            try { Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(adapter, 6))(adapter, &factoryId, &factory), "GetParent"); }
            finally { Release(ref adapter); }
            // DXGI_SWAP_CHAIN_DESC1: size, B8G8R8A8, no stereo, 1 sample, render target, 2 buffers, stretch, flip sequential, premultiplied.
            var desc = stackalloc uint[] { (uint)w, (uint)h, 87, 0, 1, 0, 0x20, 2, 0, 3, 1, 0 };
            try { Check(((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr, IntPtr*, int>)Fn(factory, 24))(factory, device, desc, IntPtr.Zero, &chain), "CreateSwapChainForComposition"); }
            finally { Release(ref factory); }
            swap = chain;
            var dcompId = new Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3");
            Check(DCompositionCreateDevice(dxgi, ref dcompId, out dcomp), "DCompositionCreateDevice");
        }
        finally { Release(ref dxgi); }
        IntPtr t, v;
        Check(((delegate* unmanaged<IntPtr, IntPtr, int, IntPtr*, int>)Fn(dcomp, 6))(dcomp, hwnd, 1, &t), "CreateTargetForHwnd"); target = t;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Fn(dcomp, 7))(dcomp, &v), "CreateVisual"); visual = v;
        Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(visual, 15))(visual, swap), "SetContent");
        Check(((delegate* unmanaged<IntPtr, IntPtr, int>)Fn(target, 3))(target, visual), "SetRoot");
        Check(((delegate* unmanaged<IntPtr, int>)Fn(dcomp, 3))(dcomp), "Commit");
        width = w; height = h;
        CreateTarget();

        var vsCode = Compile("vs", "vs_4_0"); var psCode = Compile("ps", "ps_4_0");
        try
        {
            IntPtr vs, ps, il;
            Check(((delegate* unmanaged<IntPtr, void*, nuint, IntPtr, IntPtr*, int>)Fn(device, 12))(device, BlobPointer(vsCode), BlobSize(vsCode), IntPtr.Zero, &vs), "CreateVertexShader"); vertexShader = vs;
            Check(((delegate* unmanaged<IntPtr, void*, nuint, IntPtr, IntPtr*, int>)Fn(device, 15))(device, BlobPointer(psCode), BlobSize(psCode), IntPtr.Zero, &ps), "CreatePixelShader"); pixelShader = ps;
            var semantic = Marshal.StringToHGlobalAnsi("POSITION");
            try
            {
                // D3D11_INPUT_ELEMENT_DESC: POSITION, index 0, R32G32B32_FLOAT, slot 0, offset 0, per vertex.
                var element = stackalloc IntPtr[4];
                element[0] = semantic; ((uint*)(element + 1))[0] = 0; ((uint*)(element + 1))[1] = 6; ((uint*)(element + 2))[0] = 0; ((uint*)(element + 2))[1] = 0; ((uint*)(element + 3))[0] = 0; ((uint*)(element + 3))[1] = 0;
                Check(((delegate* unmanaged<IntPtr, void*, uint, void*, nuint, IntPtr*, int>)Fn(device, 11))(device, element, 1, BlobPointer(vsCode), BlobSize(vsCode), &il), "CreateInputLayout"); layout = il;
            }
            finally { Marshal.FreeHGlobal(semantic); }
        }
        finally { Release(ref vsCode); Release(ref psCode); }

        IntPtr cb, bs, rs;
        var cbDesc = stackalloc uint[] { 80, 0, 4, 0, 0, 0 };   // constant buffer: viewProj + colour
        Check(((delegate* unmanaged<IntPtr, void*, void*, IntPtr*, int>)Fn(device, 3))(device, cbDesc, null, &cb), "CreateBuffer"); constants = cb;
        // D3D11_BLEND_DESC: premultiplied alpha (ONE, INV_SRC_ALPHA) on target 0.
        var blendDesc = stackalloc uint[66];
        blendDesc[2] = 1; blendDesc[3] = 2; blendDesc[4] = 6; blendDesc[5] = 1; blendDesc[6] = 2; blendDesc[7] = 6; blendDesc[8] = 1; blendDesc[9] = 0x0F;
        Check(((delegate* unmanaged<IntPtr, void*, IntPtr*, int>)Fn(device, 20))(device, blendDesc, &bs), "CreateBlendState"); blend = bs;
        // D3D11_RASTERIZER_DESC: solid, no culling, depth clip on (near and far planes), antialiased lines.
        var rasterDesc = stackalloc uint[] { 3, 1, 0, 0, 0, 0, 1, 0, 0, 1 };
        Check(((delegate* unmanaged<IntPtr, void*, IntPtr*, int>)Fn(device, 22))(device, rasterDesc, &rs), "CreateRasterizerState"); raster = rs;
    }

    private void CreateTarget()
    {
        IntPtr texture, view; var textureId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        Check(((delegate* unmanaged<IntPtr, uint, Guid*, IntPtr*, int>)Fn(swap, 9))(swap, 0, &textureId, &texture), "GetBuffer");
        try { Check(((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr*, int>)Fn(device, 9))(device, texture, IntPtr.Zero, &view), "CreateRenderTargetView"); rtv = view; }
        finally { Release(ref texture); }
    }

    private void Resize(int w, int h)
    {
        Call(context, 33, 0u, (IntPtr*)null, IntPtr.Zero);   // unbind the old target
        Release(ref rtv);
        Check(((delegate* unmanaged<IntPtr, uint, uint, uint, uint, uint, int>)Fn(swap, 13))(swap, 0, (uint)w, (uint)h, 0, 0), "ResizeBuffers");
        width = w; height = h;
        CreateTarget();
    }

    private void Hide()
    {
        if (!shown || hwnd == IntPtr.Zero) return;
        User32.ShowWindow(hwnd, 0); shown = false;
    }

    // The game's visible top-level window (no owner), found by process ID.
    [ThreadStatic] private static uint searchProcess;
    [ThreadStatic] private static IntPtr searchResult;
    private static IntPtr GameWindow(uint processId)
    {
        searchProcess = processId; searchResult = IntPtr.Zero;
        User32.EnumWindows(&FindGameWindow, IntPtr.Zero);
        return searchResult;
    }
    [UnmanagedCallersOnly]
    private static int FindGameWindow(IntPtr window, IntPtr parameter)
    {
        User32.GetWindowThreadProcessId(window, out var owner);
        if (owner != searchProcess || !User32.IsWindowVisible(window) || User32.GetWindow(window, 4) != IntPtr.Zero) return 1;
        searchResult = window;
        return 0;
    }

    private static void Pump()
    {
        var message = stackalloc byte[64];
        while (User32.PeekMessageW(message, IntPtr.Zero, 0, 0, 1)) { User32.TranslateMessage(message); User32.DispatchMessageW(message); }
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr window, uint message, IntPtr w, IntPtr l) => User32.DefWindowProcW(window, message, w, l);

    private IntPtr Compile(string entry, string profile)
    {
        var source = Encoding.ASCII.GetBytes(Shader);
        var hr = D3DCompile(source, (nint)source.Length, null, IntPtr.Zero, IntPtr.Zero, entry, profile, 0, 0, out var code, out var errors);
        if (hr < 0)
        {
            var text = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi((IntPtr)BlobPointer(errors), (int)BlobSize(errors)) : "";
            Release(ref errors);
            throw new InvalidOperationException("Shader compile failed: " + text);
        }
        Release(ref errors);
        return code;
    }

    private void Cleanup()
    {
        foreach (var field in new[] { vertices, raster, blend, constants, layout, pixelShader, vertexShader, rtv, visual, target, dcomp, swap, context, device })
        {
            var p = field; Release(ref p);
        }
        vertices = raster = blend = constants = layout = pixelShader = vertexShader = rtv = visual = target = dcomp = swap = context = device = IntPtr.Zero;
        if (hwnd != IntPtr.Zero) { User32.DestroyWindow(hwnd); hwnd = IntPtr.Zero; }
    }

    // COM through the vtable: slot index per the Windows SDK headers.
    private static void* Fn(IntPtr com, int slot) => (*(void***)com)[slot];
    private static void Release(ref IntPtr com) { if (com != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, uint>)Fn(com, 2))(com); com = IntPtr.Zero; } }
    private static IntPtr Query(IntPtr com, Guid id)
    {
        IntPtr result;
        Check(((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Fn(com, 0))(com, &id, &result), "QueryInterface");
        return result;
    }
    private static void* BlobPointer(IntPtr blob) => ((delegate* unmanaged<IntPtr, void*>)Fn(blob, 3))(blob);
    private static nuint BlobSize(IntPtr blob) => ((delegate* unmanaged<IntPtr, nuint>)Fn(blob, 4))(blob);
    private static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})."); }

    // ID3D11DeviceContext calls (void results).
    private static void Call(IntPtr c, int slot, IntPtr a) => ((delegate* unmanaged<IntPtr, IntPtr, void>)Fn(c, slot))(c, a);
    private static void Call(IntPtr c, int slot, uint a) => ((delegate* unmanaged<IntPtr, uint, void>)Fn(c, slot))(c, a);
    private static void Call(IntPtr c, int slot, uint a, uint b) => ((delegate* unmanaged<IntPtr, uint, uint, void>)Fn(c, slot))(c, a, b);
    private static void Call(IntPtr c, int slot, IntPtr a, float* b) => ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Fn(c, slot))(c, a, b);
    private static void Call(IntPtr c, int slot, uint a, float* b) => ((delegate* unmanaged<IntPtr, uint, float*, void>)Fn(c, slot))(c, a, b);
    private static void Call(IntPtr c, int slot, uint a, IntPtr* b, IntPtr d) => ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Fn(c, slot))(c, a, b, d);
    private static void Call(IntPtr c, int slot, uint a, uint b, IntPtr* d) => ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Fn(c, slot))(c, a, b, d);
    private static void Call(IntPtr c, int slot, uint a, uint b, IntPtr* d, uint* e, uint* f) => ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, uint*, void>)Fn(c, slot))(c, a, b, d, e, f);
    private static void Call(IntPtr c, int slot, IntPtr a, IntPtr b, uint d) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void>)Fn(c, slot))(c, a, b, d);
    private static void Call(IntPtr c, int slot, IntPtr a, float* b, uint d) => ((delegate* unmanaged<IntPtr, IntPtr, float*, uint, void>)Fn(c, slot))(c, a, b, d);
    private static void Call(IntPtr c, int slot, IntPtr a, uint b, void* d, void* e, uint f, uint g) => ((delegate* unmanaged<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)Fn(c, slot))(c, a, b, d, e, f, g);

    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);
    [DllImport("dcomp.dll")] private static extern int DCompositionCreateDevice(IntPtr dxgiDevice, ref Guid iid, out IntPtr device);
    [DllImport("d3dcompiler_47.dll")] private static extern int D3DCompile(byte[] source, nint size, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr defines, IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    private static class User32
    {
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct WndClassEx
        {
            public uint Size, Style; public IntPtr WndProc; public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
        }
        [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr window, uint key, byte alpha, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WndClassEx wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool PeekMessageW(void* message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(void* message);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(void* message);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
    }
}
