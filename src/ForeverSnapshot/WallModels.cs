// The .walls models (wowmap 3D viewer look): a translucent plane one ADT chunk edge wide (33 1/3 yards along the model's local Y)
// rising from z = 0 to the world ceiling, plus solid bars along the floor and up both ends (the floor line and corner guides).
// Unlit, unfogged, two-sided, alpha-blended, no depth write, so terrain, buildings and units in front hide them; the plain white
// interface texture white8x8 is tinted by M2 colours (face alpha 0.069 red / 0.059 yellow, lines 0.96).
// Format (wowdev M2 / SKIN, mirroring a 70338 client model: MD21 with MD20 version 272, SFID, TXID; skin header of 64 bytes):
// one bone, one inline Stand sequence, two colours, one texture, one material, two skin sections and batches (face, lines).
using System.Buffers.Binary;
using System.Text;
using AuthServer.Network;

namespace ForeverSnapshot;

internal static class WallModels
{
    private const float Width = ForeverWallModels.Width, Height = ForeverWallModels.Height, Bar = ForeverWallModels.Bar;
    public const int WhiteTexture = 130871;   // interface/buttons/white8x8.blp
    private const ushort None = 0xFFFF;
    private const int HeaderSize = 0x130;

    private sealed class Mesh
    {
        public readonly List<(float x, float y, float z)> Vertices = new();
        public readonly List<ushort> Indices = new();
        // A two-sided quad from four corners (a, b, c, d in order around it).
        public void Quad((float, float, float) a, (float, float, float) b, (float, float, float) c, (float, float, float) d)
        {
            var i = (ushort)Vertices.Count;
            Vertices.AddRange(new[] { a, b, c, d });
            Indices.AddRange(new[] { i, (ushort)(i + 1), (ushort)(i + 2), i, (ushort)(i + 2), (ushort)(i + 3) });
        }
    }

    // Section 0: the face. Section 1: floor bar (an upright and a flat strip) and a crossed pair of upright strips at each end.
    private static (Mesh face, Mesh lines) Geometry()
    {
        float w = Width / 2, h = Height, t = Bar / 2;
        var face = new Mesh();
        face.Quad((0, -w, 0), (0, w, 0), (0, w, h), (0, -w, h));
        var lines = new Mesh();
        lines.Quad((0, -w, 0), (0, w, 0), (0, w, Bar), (0, -w, Bar));
        lines.Quad((-t, -w, t), (t, -w, t), (t, w, t), (-t, w, t));
        foreach (var y in new[] { -w, w })
        {
            lines.Quad((0, y - t, 0), (0, y + t, 0), (0, y + t, h), (0, y - t, h));
            lines.Quad((-t, y, 0), (t, y, 0), (t, y, h), (-t, y, h));
        }
        return (face, lines);
    }

    // One skin, shared by both colours.
    public static byte[] Skin()
    {
        var (face, lines) = Geometry();
        var count = face.Vertices.Count + lines.Vertices.Count;
        var indices = face.Indices.Concat(lines.Indices.Select(i => (ushort)(i + face.Vertices.Count))).ToList();
        var w = new Blob(64);
        var vertexList = w.Align().Here; for (var i = 0; i < count; i++) w.U16((ushort)i);
        var indexList = w.Align().Here; foreach (var i in indices) w.U16(i);
        var boneList = w.Align().Here; for (var i = 0; i < count; i++) w.U32(0);   // four bone indices (all bone 0)
        var sections = w.Align().Here;
        Section(w, 0, face.Vertices.Count, 0, face.Indices.Count, face.Vertices);
        Section(w, face.Vertices.Count, lines.Vertices.Count, face.Indices.Count, lines.Indices.Count, lines.Vertices);
        var batches = w.Align().Here;
        for (ushort s = 0; s < 2; s++)
            w.U8(0).U8(0).U16(0).U16(s).U16(0).U16(s).U16(0).U16(0).U16(1).U16(0).U16(None).U16(0).U16(0);   // section s, colour s, material 0, texture 0
        var skin = w.ToArray();
        "SKIN"u8.CopyTo(skin);
        Array(skin, 4, count, vertexList); Array(skin, 12, indices.Count, indexList); Array(skin, 20, count, boneList);
        Array(skin, 28, 2, sections); Array(skin, 36, 2, batches);
        return skin;   // bone count max 0 and no shadow batches, as the client's own skins
    }

    private static void Section(Blob w, int vertexStart, int vertexCount, int indexStart, int indexCount, List<(float x, float y, float z)> vertices)
    {
        var (centre, radius) = Bounds(vertices);
        w.U16(0).U16(0).U16((ushort)vertexStart).U16((ushort)vertexCount).U16((ushort)indexStart).U16((ushort)indexCount)
            .U16(1).U16(0).U16(1).U16(0)                                                           // one bone, combo 0, one influence, centre bone 0
            .F(centre.x).F(centre.y).F(centre.z).F(centre.x).F(centre.y).F(centre.z).F(radius);
    }

    private static ((float x, float y, float z) centre, float radius) Bounds(IEnumerable<(float x, float y, float z)> vertices)
    {
        var list = vertices.ToList();
        (float x, float y, float z) min = (list.Min(v => v.x), list.Min(v => v.y), list.Min(v => v.z)), max = (list.Max(v => v.x), list.Max(v => v.y), list.Max(v => v.z));
        var centre = ((min.x + max.x) / 2, (min.y + max.y) / 2, (min.z + max.z) / 2);
        var radius = MathF.Sqrt(MathF.Pow(max.x - min.x, 2) + MathF.Pow(max.y - min.y, 2) + MathF.Pow(max.z - min.z, 2)) / 2;
        return (centre, radius);
    }

    // wowmap colours (RGBA): faces and lines of red walls (next to impassable or missing ground) and yellow walls (next to walkable).
    public static readonly float[] RedFace = { 1f, 0.20f, 0.14f, 0.069f }, RedLine = { 1f, 0.05f, 0.04f, 0.96f };
    public static readonly float[] YellowFace = { 1f, 0.85f, 0.20f, 0.059f }, YellowLine = { 1f, 0.88f, 0.02f, 0.96f };

    // A face colour for one glass tint (ForeverWallModels.Tints): the wowmap face colour lightened towards white, with the tint's alpha.
    public static float[] Face(int tint, bool red)
    {
        var (_, yellowAlpha, redAlpha, lighten) = ForeverWallModels.Tints[tint];
        var face = red ? RedFace : YellowFace;
        return new[] { face[0] + (1 - face[0]) * lighten, face[1] + (1 - face[1]) * lighten, face[2] + (1 - face[2]) * lighten, red ? redAlpha : yellowAlpha };
    }

    // The model for one colour pair; skinFileDataId names the shared skin.
    public static byte[] Model(float[] faceColour, float[] lineColour, int skinFileDataId)
    {
        var (face, lines) = Geometry();
        var all = face.Vertices.Concat(lines.Vertices).ToList();
        var (centre, radius) = Bounds(all);
        var w = new Blob(HeaderSize);
        var header = new Dictionary<int, (int count, int offset)>();

        // Stand (inline: flag 0x20; the other bits as the client's own model), looping, blend time 150, the model's bounds.
        header[0x1C] = (1, w.Align().Here);
        w.U16(0).U16(0).U32(3333).F(0).U32(0x8A0).U16(0x7FFF).U16(0).U32(0).U32(0).U16(150).U16(0);
        WriteBox(w, all); w.F(radius).U16(None).U16(0);
        // No sequence lookup table (as the client's own model): the client then scans the sequences. A table is a hash with quadratic
        // probing that ends only at a match or an empty (0xFFFF) slot; one slot holding Stand made the lookup of animation 0x9E spin
        // forever (WowB 0x1df4d40, ERROR #109 freeze).

        // One bone without animation: key bone -1, no parent, empty tracks (one empty key list per sequence).
        var bone = w.Align().Here;
        w.U32(0xFFFFFFFF).U32(0).U16(None).U16(0).U32(0);
        var tracks = new List<int>();
        for (var i = 0; i < 3; i++) { tracks.Add(w.Here); w.U16(0).U16(None).U32(0).U32(0).U32(0).U32(0); }
        w.F(0).F(0).F(0);
        header[0x2C] = (1, bone);
        foreach (var track in tracks) EmptyTrack(w, track);
        header[0x34] = (1, w.Align().Here); w.U16(None);                                                // key bone lookup

        header[0x3C] = (all.Count, w.Align().Here);
        foreach (var v in all) w.F(v.x).F(v.y).F(v.z).U8(255).U8(0).U8(0).U8(0).U32(0).F(1).F(0).F(0).F(v.y > 0 ? 1 : 0).F(v.z > 0 ? 1 : 0).F(0).F(0);

        // Colours: 0 = face, 1 = lines; one key each at time 0.
        var colours = w.Align().Here;
        var colourTracks = new List<(int at, bool vector, float[] value)>();
        foreach (var colour in new[] { faceColour, lineColour })
        {
            colourTracks.Add((w.Here, true, colour[..3])); w.U16(0).U16(None).U32(0).U32(0).U32(0).U32(0);
            colourTracks.Add((w.Here, false, new[] { colour[3] })); w.U16(0).U16(None).U32(0).U32(0).U32(0).U32(0);
        }
        header[0x48] = (2, colours);
        foreach (var (at, vector, value) in colourTracks) KeyTrack(w, at, vector ? value : null, vector ? (short)0 : Fixed16(value[0]));
        header[0x50] = (1, w.Align().Here); w.U32(0).U32(3).U32(0).U32(0);                             // texture: hardcoded (TXID), wrap U and V
        var weight = w.Align().Here; w.U16(0).U16(None).U32(0).U32(0).U32(0).U32(0);
        header[0x58] = (1, weight); KeyTrack(w, weight, null, 0x7FFF);
        header[0x68] = (1, w.Align().Here); w.U16(0);                                                   // replaceable texture lookup
        header[0x70] = (1, w.Align().Here); w.U16(0x17).U16(2);                                         // unlit, unfogged, two-sided, no depth write; alpha blend
        header[0x78] = (1, w.Align().Here); w.U16(0);                                                   // bone lookup
        header[0x80] = (1, w.Align().Here); w.U16(0);                                                   // texture lookup
        header[0x90] = (1, w.Align().Here); w.U16(0);                                                   // transparency lookup
        header[0x98] = (1, w.Align().Here); w.U16(None);                                                // no texture transform

        var md20 = w.ToArray();
        "MD20"u8.CopyTo(md20);
        BinaryPrimitives.WriteUInt32LittleEndian(md20.AsSpan(4), 272);
        BinaryPrimitives.WriteUInt32LittleEndian(md20.AsSpan(0x44), 1);                                // one skin profile
        foreach (var (field, (count, offset)) in header) Array(md20, field, count, offset);
        var box = new Blob(0); WriteBox(box, all); box.F(radius);
        box.ToArray().CopyTo(md20, 0xA0);                                                               // bounding box and radius (no collision)

        using var output = new MemoryStream();
        Chunk(output, "MD21", md20);
        Chunk(output, "SFID", BitConverter.GetBytes(skinFileDataId));
        Chunk(output, "TXID", BitConverter.GetBytes(WhiteTexture));
        return output.ToArray();
    }

    private static void WriteBox(Blob w, List<(float x, float y, float z)> v) =>
        w.F(v.Min(p => p.x)).F(v.Min(p => p.y)).F(v.Min(p => p.z)).F(v.Max(p => p.x)).F(v.Max(p => p.y)).F(v.Max(p => p.z));

    private static short Fixed16(float value) => (short)Math.Clamp((int)MathF.Round(value * 32767), 0, 32767);

    // M2Track with one sequence: timestamps and values are one inner array each (empty here).
    private static void EmptyTrack(Blob w, int track)
    {
        var times = w.Align().Here; w.U32(0).U32(0);
        var values = w.Align().Here; w.U32(0).U32(0);
        w.Patch(track + 4, 1, times); w.Patch(track + 12, 1, values);
    }

    // M2Track with one sequence and one key at time 0: a C3Vector (vector) or a fixed16.
    private static void KeyTrack(Blob w, int track, float[] vector, short value)
    {
        var time = w.Align().Here; w.U32(0);
        var data = w.Align().Here; if (vector != null) foreach (var f in vector) w.F(f); else w.U16((ushort)value);
        var times = w.Align().Here; w.U32(1).U32((uint)time);
        var values = w.Align().Here; w.U32(1).U32((uint)data);
        w.Patch(track + 4, 1, times); w.Patch(track + 12, 1, values);
    }

    private static void Array(byte[] target, int field, int count, int offset)
    {
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(field), count);
        BinaryPrimitives.WriteInt32LittleEndian(target.AsSpan(field + 4), count == 0 ? 0 : offset);
    }

    private static void Chunk(Stream output, string tag, byte[] data)
    {
        output.Write(Encoding.ASCII.GetBytes(tag)); output.Write(BitConverter.GetBytes(data.Length)); output.Write(data);
    }

    // Little-endian blob with 16-byte aligned arrays (as the client's own files) and in-place (count, offset) patches.
    private sealed class Blob
    {
        private readonly MemoryStream data = new();
        public Blob(int reserved) { data.SetLength(reserved); data.Position = reserved; }
        public int Here => (int)data.Position;
        public Blob Align() { while (data.Position % 16 != 0) data.WriteByte(0); return this; }
        public Blob U8(byte v) { data.WriteByte(v); return this; }
        public Blob U16(ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); data.Write(b); return this; }
        public Blob U32(uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); data.Write(b); return this; }
        public Blob F(float v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(b, v); data.Write(b); return this; }
        public void Patch(int at, int count, int offset)
        {
            var position = data.Position; data.Position = at; U32((uint)count).U32((uint)offset); data.Position = position;
        }
        public byte[] ToArray() => data.ToArray();
    }
}
