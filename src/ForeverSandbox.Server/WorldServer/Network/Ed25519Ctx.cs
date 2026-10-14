// Independent RFC 8032 Ed25519 / Ed25519ctx (dom2 with phflag 0). Reference-style
// BigInteger arithmetic: one signature per world login, not a hot path, and not
// constant time. The only signing key used is the published interop key in
// ForeverWorldKeys, so timing exposes nothing secret. Verified by RFC vectors.
using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AuthServer.Network;

internal static class Ed25519Ctx
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point Base = Decompress(Encode(new BigInteger(4) * Inverse(5) % P, 0))
        ?? throw new InvalidOperationException("Ed25519 base point failed to decode.");
    private static readonly byte[] Dom2Prefix = Encoding.ASCII.GetBytes("SigEd25519 no Ed25519 collisions");

    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    public static byte[] PublicKey(byte[] seed) { var (scalar, _) = Expand(seed); return Compress(Multiply(scalar, Base)); }

    public static byte[] Sign(byte[] seed, byte[] message, byte[] context)
    {
        var dom = Dom2(context);
        var (scalar, prefix) = Expand(seed);
        var publicKey = Compress(Multiply(scalar, Base));
        var r = Scalar(SHA512.HashData(Concat(dom, prefix, message)));
        var encodedR = Compress(Multiply(r, Base));
        var h = Scalar(SHA512.HashData(Concat(dom, encodedR, publicKey, message)));
        var s = (r + h * scalar) % L;
        return Concat(encodedR, Little(s));
    }

    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature, byte[] context)
    {
        if (publicKey?.Length != 32 || signature?.Length != 64) return false;
        var a = Decompress(publicKey); var r = Decompress(signature[..32]);
        var s = new BigInteger(signature.AsSpan(32), true, false);
        if (a == null || r == null || s >= L) return false;
        var h = Scalar(SHA512.HashData(Concat(Dom2(context), signature[..32], publicKey, message)));
        return Same(Multiply(s, Base), Add(r.Value, Multiply(h, a.Value)));
    }

    private static byte[] Dom2(byte[] context)
    {
        // Ed25519ctx requires a non-empty context; plain Ed25519 is not used here.
        if (context == null || context.Length is 0 or > 255) throw new ArgumentException("Ed25519ctx context must be 1..255 bytes.");
        return Concat(Dom2Prefix, new byte[] { 0, (byte)context.Length }, context);
    }

    private static (BigInteger scalar, byte[] prefix) Expand(byte[] seed)
    {
        if (seed?.Length != 32) throw new ArgumentException("Ed25519 seed must be 32 bytes.");
        var hash = SHA512.HashData(seed);
        hash[0] &= 248; hash[31] &= 63; hash[31] |= 64;
        return (new BigInteger(hash.AsSpan(0, 32), true, false), hash[32..]);
    }

    private static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X)); var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D); var d = Mod(2 * p.Z * q.Z);
        var e = b - a; var f = d - c; var g = d + c; var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(BigInteger scalar, Point point)
    {
        var result = new Point(0, 1, 1, 0);
        while (scalar > 0) { if (!scalar.IsEven) result = Add(result, point); point = Add(point, point); scalar >>= 1; }
        return result;
    }

    private static bool Same(Point p, Point q) => Mod(p.X * q.Z - q.X * p.Z) == 0 && Mod(p.Y * q.Z - q.Y * p.Z) == 0;

    private static byte[] Compress(Point point)
    {
        var inverse = Inverse(point.Z);
        return Encode(Mod(point.Y * inverse), Mod(point.X * inverse).IsEven ? 0 : 1);
    }

    private static byte[] Encode(BigInteger y, int sign) { var bytes = Little(y); bytes[31] |= (byte)(sign << 7); return bytes; }

    private static Point? Decompress(byte[] encoded)
    {
        var copy = encoded.ToArray(); var sign = copy[31] >> 7; copy[31] &= 0x7F;
        var y = new BigInteger(copy, true, false);
        if (y >= P) return null;
        var x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = Mod(x * SqrtMinusOne);
        if (Mod(x * x - x2) != 0 || x.IsZero && sign == 1) return null;
        if ((x.IsEven ? 0 : 1) != sign) x = P - x;
        return new Point(x, y, 1, Mod(x * y));
    }

    private static BigInteger Scalar(byte[] hash) => new BigInteger(hash, true, false) % L;
    private static BigInteger Mod(BigInteger value) { var result = value % P; return result.Sign < 0 ? result + P : result; }
    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);
    private static byte[] Little(BigInteger value) { var bytes = new byte[32]; value.TryWriteBytes(bytes, out _, true, false); return bytes; }
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}
