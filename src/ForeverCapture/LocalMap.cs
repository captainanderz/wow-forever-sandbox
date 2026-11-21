// The live map window, on this PC only. A small web server on localhost serves the WoW Atlas 2D map and 3D viewer
// read-only from the site's folder, adds this tool's own scripts to them (LiveMap.js, Objects3D.js), and answers those
// with the captures' coverage, the live position and the captured objects. In 3D the objects are drawn by the renderer
// build kept with this tool (artifacts\viewer3d-objects, made by tools\viewer3d-objects\patch-viewer3d-objects.ps1), and
// their models are read from the game's own storage. Nothing is written to the site's folder and the site knows nothing
// of this.
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AuthServer.Network;

namespace ForeverCapture;

internal sealed class LocalMap : IDisposable
{
    public const int Port = 5298;
    private const string MapScript = "<script src=\"/__local/live-map.js\"></script>", ViewerScript = "<script src=\"/__local/objects-3d.js\"></script>";
    // GameObjectDisplayInfo: field 1 is the model's FileDataID (field 0 its box).
    private const int DisplayTable = 1266277, DisplayModelField = 1;
    private readonly HttpListener listener = new();
    private readonly string web, tiles, captures, wow, client, renderer;
    private readonly object gate = new();
    private ForeverCasc casc;
    private GameCdn cdn;
    private ForeverDb2 displays;
    private JsonDocument versions;
    // Every file the objects' models load: the viewer asks for these as missing (its own export has none of them).
    private readonly HashSet<int> objectFiles = new();
    private readonly Dictionary<int, (int Model, int[] Files)?> models = new();
    public string Url { get; }

    public LocalMap(string siteFolder, string captureRoot, string wowFolder, string clientVersion)
    {
        wow = wowFolder; client = clientVersion;
        renderer = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "viewer3d-objects");
        web = Path.GetFullPath(Path.Combine(siteFolder, "WebApp", "wwwroot"));
        tiles = Path.GetFullPath(Path.Combine(siteFolder, "tiles"));
        if (!File.Exists(Path.Combine(web, "index.html")) || !Directory.Exists(tiles))
            throw new IOException($"No WoW Atlas site in {siteFolder} (expected WebApp\\wwwroot\\index.html and tiles).");
        captures = captureRoot;
        Url = $"http://localhost:{Port}/";
        listener.Prefixes.Add(Url);
        try { listener.Start(); }
        catch (HttpListenerException error) { throw new InvalidOperationException($"The map window's port {Port} is in use ({error.Message}). Is another Forever Capture running?"); }
        _ = Serve();
    }

    /// <summary>Opens the window: an Edge app window (no tabs or address bar), else the default browser.</summary>
    public void OpenWindow(bool live)
    {
        var page = Url + "?" + (live ? "live=1&" : "") + "product=" + ObjectsProduct;
        var edge = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
            .Select(root => Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe")).FirstOrDefault(File.Exists);
        try
        {
            if (edge != null) Process.Start(new ProcessStartInfo(edge) { ArgumentList = { "--app=" + page, "--window-size=560,620" }, UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception) { Console.WriteLine("Open the map yourself: " + page); }
    }

    public const string ObjectsProduct = "wow_classic_beta";

    // Takes over from an old worker at once, deletes this origin's caches, unregisters and reloads the pages that worker
    // served. A page it did not serve (the site's pages register /sw.js on every load) is left alone, so nothing loops.
    internal const string RemoveServiceWorker = """
        self.addEventListener('install', function () { self.skipWaiting(); });
        self.addEventListener('activate', function (event) {
            event.waitUntil(caches.keys()
                .then(function (keys) { return Promise.all(keys.map(function (key) { return caches.delete(key); })); })
                .then(function () { return self.registration.unregister(); })
                .then(function () { return self.clients.matchAll({ type: 'window' }); })
                .then(function (pages) { pages.forEach(function (page) { page.navigate(page.url); }); }));
        });
        """;

    private async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Answer(context));
        }
    }

    private void Answer(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            var path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath);
            if (context.Request.HttpMethod != "GET") { response.StatusCode = 405; return; }
            if (path == "/api/config") { Send(response, "application/json", Encoding.UTF8.GetBytes("{\"tileBaseUrl\":\"/tiles\"}"), false); return; }
            // No service worker here: the site's would answer the objects' model files with its own 404 and hand out stale copies
            // of this tool's scripts and lists. A 404 for /sw.js does not remove one Edge registered earlier, so /sw.js removes itself.
            if (path == "/sw.js") { Send(response, "text/javascript; charset=utf-8", Encoding.UTF8.GetBytes(RemoveServiceWorker), false); return; }
            if (path.StartsWith("/api/", StringComparison.Ordinal)) { response.StatusCode = 404; return; }
            if (path is "/__local/live-map.js" or "/__local/objects-3d.js")
            {
                using var stream = typeof(LocalMap).Assembly.GetManifestResourceStream(path == "/__local/live-map.js" ? "LiveMap.js" : "Objects3D.js")!;
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                Send(response, "text/javascript; charset=utf-8", memory.ToArray(), false);
                return;
            }
            if (path.StartsWith("/__local/objects/", StringComparison.Ordinal) && int.TryParse(Path.GetFileNameWithoutExtension(path), out var wdt))
            {
                Send(response, "application/json", Objects(wdt, context.Request.QueryString["product"] ?? ObjectsProduct), false);
                return;
            }
            // The objects' model files: the viewer asks for them as missing, and they come from the game's own storage.
            if (path.StartsWith("/__viewer3d_missing_asset/id/", StringComparison.Ordinal) && int.TryParse(path[29..], out var fid))
            {
                bool known;
                lock (gate) known = objectFiles.Contains(fid);
                var stored = known ? GameFile(Game(), fid) : null;
                if (stored == null) { response.StatusCode = 404; return; }
                Send(response, "application/octet-stream", stored, false);
                return;
            }
            // The renderer build that can draw the objects (the site's own build plus that one addition), when it is here.
            if (path.StartsWith("/vendor/webwowviewer/", StringComparison.Ordinal) && Resolve(Path.GetFullPath(renderer), path[21..]) is { } build && File.Exists(build))
            {
                Send(response, ContentType(build), File.ReadAllBytes(build), false);
                return;
            }
            if (path == "/__local/live.json")
            {
                var file = Path.Combine(captures, "live.json");
                if (!File.Exists(file)) { response.StatusCode = 404; return; }
                Send(response, "application/json", ReadShared(file), false);
                return;
            }
            if (path.StartsWith("/__local/coverage/", StringComparison.Ordinal) && int.TryParse(Path.GetFileNameWithoutExtension(path), out var mapId))
            {
                Send(response, "application/json", Coverage(captures, mapId), false);
                return;
            }
            var local = path.StartsWith("/tiles/", StringComparison.Ordinal) ? Resolve(tiles, path[7..]) : Resolve(web, path == "/" ? "index.html" : path);
            if (local == null || !File.Exists(local)) { response.StatusCode = 404; return; }
            var type = ContentType(local);
            var bytes = File.ReadAllBytes(local);
            var page = Path.GetFileName(local).ToLowerInvariant();
            if (page is "index.html" or "viewer3d.html") bytes = Encoding.UTF8.GetBytes(Inject(Encoding.UTF8.GetString(bytes), page == "index.html" ? MapScript : ViewerScript));
            Send(response, type, bytes, type.StartsWith("image/", StringComparison.Ordinal));
        }
        // One request that fails (an unreadable game file, a closed window) must not stop the others: it answers 500 and says why.
        catch (Exception error)
        {
            Console.Error.WriteLine($"Map window: {context.Request.Url?.AbsolutePath}: {error.GetType().Name}: {error.Message}");
            try { response.StatusCode = 500; } catch (InvalidOperationException) { }
        }
        finally
        {
            try { response.Close(); } catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { }
        }
    }

    private static void Send(HttpListenerResponse response, string type, byte[] body, bool cacheable)
    {
        response.ContentType = type;
        response.Headers["Cache-Control"] = cacheable ? "max-age=3600" : "no-store";
        response.ContentLength64 = body.Length;
        response.OutputStream.Write(body);
    }

    // The recording swaps live.json in while it may be read.
    private static byte[] ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>A file inside <paramref name="root"/> for a request path, or null for one that leaves it.</summary>
    internal static string Resolve(string root, string requestPath)
    {
        if (requestPath.Contains('\0') || requestPath.Contains(':')) return null;
        var full = Path.GetFullPath(Path.Combine(root, requestPath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>The site's page with this tool's script added at the end of its body.</summary>
    internal static string Inject(string html, string script)
    {
        var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? html + script : html[..end] + script + html[end..];
    }

    private ForeverCasc Game()
    {
        lock (gate)
        {
            if (casc != null) return casc;
            Console.WriteLine("Map window: opening the game's files for the objects' models...");
            casc = ForeverCasc.Open(wow, client, _ => null);
            displays = new ForeverDb2(casc.OpenFile(DisplayTable));
            try { cdn = GameCdn.Open(wow, client, out var reason); if (cdn == null) Console.WriteLine($"Map window: {reason}; files the game streams instead of storing will be missing."); }
            catch (Exception error) when (error is IOException or InvalidDataException or KeyNotFoundException or UnauthorizedAccessException)
            { Console.WriteLine($"Map window: the build's CDN index could not be read ({error.Message}); files the game streams will be missing."); }
            return casc;
        }
    }

    // A game file from the disk, else from the CDN for one the build lists but the game streams (textures drew black without).
    private byte[] GameFile(ForeverCasc game, int fileDataId) => game.TryOpenFile(fileDataId) ?? (game.Has(fileDataId) ? cdn?.Fetch(game, fileDataId) : null);

    /// <summary>
    /// The captured objects of a map for the 3D viewer: rows of [model FileDataID, x, y, z, qx, qy, qz, qw, scale, id] in
    /// world coordinates. Objects whose model is not an M2 of its own (a WMO, or a creature model whose skin lives in the
    /// creature tables and would draw black) are counted, not drawn; so are a WMO-only map's (a dungeon: its objects stand
    /// in map coordinates, the viewer's building in its own).
    /// shortcut: dungeons and other WMO-only maps get none; move each object by the WDT's MODF placement once a dungeon
    /// capture exists to check it against.
    /// </summary>
    private byte[] Objects(int wdt, string product)
    {
        var mapId = MapIdOf(wdt, product);
        var game = Game();
        if (mapId == null || WmoOnly(game.TryOpenFile(wdt)))
            return JsonSerializer.SerializeToUtf8Bytes(new { rows = Array.Empty<double>(), drawn = 0, skipped = 0, wmoOnly = mapId != null });
        var placed = Placed(captures, mapId.Value);
        var rows = new List<double>();
        var skipped = 0;
        foreach (var o in placed)
        {
            var model = ModelOf(game, o.Display);
            if (model == null) { skipped++; continue; }
            rows.AddRange(new[] { model.Value.Model, o.X, o.Y, o.Z, o.Rotation[0], o.Rotation[1], o.Rotation[2], o.Rotation[3], o.Scale, 0xE0000000u + (double)(rows.Count / 10) });
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { rows, drawn = rows.Count / 10, skipped, wmoOnly = false });
    }

    private (int Model, int[] Files)? ModelOf(ForeverCasc game, int display)
    {
        lock (gate)
        {
            if (models.TryGetValue(display, out var known)) return known;
            var model = displays.Get(display, DisplayModelField);
            var bytes = model > 0 ? GameFile(game, model) : null;
            (int, int[])? result = null;
            if (bytes != null && M2Files(bytes, out var outside) is { } first && !outside)
            {
                // Skeletons name further files of their own (a parent skeleton, animations, bones).
                var files = new SortedSet<int> { model };
                var queue = new Queue<int>(first);
                while (queue.Count > 0)
                {
                    var id = queue.Dequeue();
                    if (!files.Add(id) || !game.Has(id)) continue;
                    if (GameFile(game, id) is { } more && Chunked(more)) foreach (var next in M2Files(more, out _)) queue.Enqueue(next);
                }
                files.RemoveWhere(id => !game.Has(id));
                result = (model, files.ToArray());
                objectFiles.UnionWith(files);
            }
            models[display] = result;
            return result;
        }
    }

    private int? MapIdOf(int wdt, string product)
    {
        lock (gate) versions ??= JsonDocument.Parse(File.ReadAllBytes(Path.Combine(web, "versions.json")));
        var root = versions.RootElement;
        // The site's map list is an array (an object keyed by entry in older copies).
        foreach (var entry in root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : root.EnumerateObject().Select(p => p.Value))
            if (entry.TryGetProperty("map", out var map) && map.TryGetProperty("WdtFileDataID", out var id) && id.GetInt32() == wdt &&
                map.TryGetProperty("product", out var owner) && owner.GetString() == product)
                return map.GetProperty("ID").GetInt32();
        return null;
    }

    private static bool Chunked(byte[] file) => file.Length >= 8 && file[0] is >= (byte)'A' and <= (byte)'Z' && file[3] is >= (byte)'0' and <= (byte)'Z';

    /// <summary>
    /// The files a chunked M2 or skeleton names: skins (SFID), textures (TXID), animations (AFID), skeleton (SKID), bones
    /// (BFID) and a skeleton's parent (SKPD). <paramref name="outsideTexture"/>: the model takes a texture from outside its
    /// own files (a type other than 0 in MD20, e.g. a creature skin).
    /// </summary>
    internal static List<int> M2Files(byte[] file, out bool outsideTexture)
    {
        var found = new List<int>();
        outsideTexture = false;
        for (var at = 0; at + 8 <= file.Length;)
        {
            var tag = Encoding.ASCII.GetString(file, at, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at + 4));
            if (size < 0 || at + 8 + size > file.Length) break;
            var body = file.AsSpan(at + 8, size);
            switch (tag)
            {
                case "MD21":
                    if (body.Length >= 0x58 && body[..4].SequenceEqual("MD20"u8))
                    {
                        int count = BinaryPrimitives.ReadInt32LittleEndian(body[0x50..]), offset = BinaryPrimitives.ReadInt32LittleEndian(body[0x54..]);
                        if (count is > 0 and < 1024 && offset >= 0 && offset + count * 16L <= body.Length)
                            for (var i = 0; i < count; i++) outsideTexture |= BinaryPrimitives.ReadInt32LittleEndian(body[(offset + i * 16)..]) != 0;
                    }
                    break;
                case "SFID": case "TXID": case "SKID": case "BFID":
                    for (var i = 0; i + 4 <= body.Length; i += 4) found.Add(BinaryPrimitives.ReadInt32LittleEndian(body[i..]));
                    break;
                case "AFID":
                    for (var i = 0; i + 8 <= body.Length; i += 8) found.Add(BinaryPrimitives.ReadInt32LittleEndian(body[(i + 4)..]));
                    break;
                case "SKPD":
                    if (body.Length >= 12) found.Add(BinaryPrimitives.ReadInt32LittleEndian(body[8..]));
                    break;
            }
            at += 8 + size;
        }
        found.RemoveAll(id => id <= 0);
        return found;
    }

    /// <summary>True for a WDT whose map is one building (MPHD flag 0x1): a dungeon or other WMO-only map.</summary>
    internal static bool WmoOnly(byte[] wdt)
    {
        for (var at = 0; wdt != null && at + 12 <= wdt.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(wdt.AsSpan(at + 4));
            if (Encoding.ASCII.GetString(wdt, at, 4) is "DHPM" or "MPHD") return (BinaryPrimitives.ReadUInt32LittleEndian(wdt.AsSpan(at + 8)) & 1) != 0;
            if (size < 0) break;
            at += 8 + size;
        }
        return false;
    }

    internal readonly record struct Placed3(int Display, double X, double Y, double Z, double[] Rotation, double Scale);

    /// <summary>
    /// Every captured object of a map, once: the same entry at the same spot (to the centimetre) from several captures is
    /// one object, the latest capture's sighting. The server gives an object a new GUID after each restart, so the GUID
    /// cannot tell sightings apart.
    /// </summary>
    internal static List<Placed3> Placed(string captureRoot, int mapId)
    {
        var spots = new Dictionary<(int, long, long, long), Placed3>();
        foreach (var folder in Directory.Exists(captureRoot) ? Directory.GetDirectories(captureRoot).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>())
        {
            var found = Path.Combine(folder, "gameobjects.json");
            if (!File.Exists(found)) continue;
            try
            {
                // A recording replaces its file while it may be read.
                using var document = JsonDocument.Parse(ReadShared(found));
                foreach (var o in document.RootElement.GetProperty("objects").EnumerateArray())
                {
                    if (o.GetProperty("map").GetInt32() != mapId) continue;
                    double x = o.GetProperty("x").GetDouble(), y = o.GetProperty("y").GetDouble(), z = o.GetProperty("z").GetDouble();
                    var rotation = o.GetProperty("rotation").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                    if (rotation.Length != 4) continue;
                    spots[(o.GetProperty("entry").GetInt32(), (long)Math.Round(x * 100), (long)Math.Round(y * 100), (long)Math.Round(z * 100))] =
                        new Placed3(o.GetProperty("display").GetInt32(), x, y, z, rotation, o.GetProperty("scale").GetDouble());
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException) { }   // a capture that cannot be read is left out
        }
        return spots.Values.ToList();
    }

    /// <summary>
    /// One map's coverage from every capture folder under <paramref name="captureRoot"/>: each captured object's spot once
    /// ([x, y], to a tenth of a yard) and each stretch of the character's path (x, y, x, y, ...).
    /// </summary>
    internal static byte[] Coverage(string captureRoot, int mapId)
    {
        var objects = new SortedSet<(double X, double Y)>();
        var tracks = new List<List<double>>();
        static double R(JsonElement value) => Math.Round(value.GetDouble(), 1, MidpointRounding.AwayFromZero);
        foreach (var folder in Directory.Exists(captureRoot) ? Directory.GetDirectories(captureRoot).OrderBy(f => f, StringComparer.Ordinal) : Enumerable.Empty<string>())
        {
            try
            {
                var found = Path.Combine(folder, "gameobjects.json");
                if (File.Exists(found))
                {
                    using var document = JsonDocument.Parse(ReadShared(found));
                    foreach (var o in document.RootElement.GetProperty("objects").EnumerateArray())
                        if (o.GetProperty("map").GetInt32() == mapId) objects.Add((R(o.GetProperty("x")), R(o.GetProperty("y"))));
                }
                var walked = Path.Combine(folder, "track.json");
                if (File.Exists(walked))
                {
                    using var document = JsonDocument.Parse(File.ReadAllBytes(walked));
                    foreach (var segment in document.RootElement.GetProperty("segments").EnumerateArray())
                        if (segment.GetProperty("map").GetInt32() == mapId)
                            tracks.Add(segment.GetProperty("points").EnumerateArray().SelectMany(p => new[] { R(p[0]), R(p[1]) }).ToList());
                }
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException) { }   // a capture that cannot be read is left out
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, generator = "coverage/1", mapId, objects = objects.Select(o => new[] { o.X, o.Y }), tracks });
    }

    private static string ContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
        ".json" or ".webmanifest" => "application/json", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
        ".svg" => "image/svg+xml", ".ico" => "image/x-icon", ".woff2" => "font/woff2", ".mp4" => "video/mp4", ".wasm" => "application/wasm",
        _ => "application/octet-stream"
    };

    public void Dispose()
    {
        try { listener.Close(); } catch (ObjectDisposedException) { }
        lock (gate) { casc?.Dispose(); cdn?.Dispose(); versions?.Dispose(); }
    }
}
