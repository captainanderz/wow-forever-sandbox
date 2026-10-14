// Independent SRP-v2/SHA-256 demo-account implementation. Mathematical parameters,
// field conventions and the signed PBKDF exponent / evidence padding are recorded
// in pinned 5a125dfe SRP6.{h,cpp}, BigNumber.{h,cpp} and Login.proto references.
using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace AuthServer.Network;

internal sealed class DemoSrpSession : IDisposable
{
    // Local sandbox login: admin / admin. "admin@admin" is accepted too for login screens that want an email-style name;
    // the SRP username comes from the server challenge, so either typed form proves the same password.
    public const string AccountName = "admin", Password = "admin";
    public static bool IsAccepted(string account) => string.Equals(account, AccountName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(account, AccountName + "@" + AccountName, StringComparison.OrdinalIgnoreCase);
    public const int Iterations = 15000, Width = 256;
    public const string ModulusHex = "AC6BDB41324A9A9BF166DE5E1389582FAF72B6651987EE07FC3192943DB56050A37329CBB4A099ED8193E0757767A13DD52312AB4B03310DCD7F48A9DA04FD50E8083969EDB767B0CF6095179A163AB3661A05FBD5FAAAE82918A9962F0B93B855F97993EC975EEAA80D740ADBF4FF747359D041D5C33EA71D281E446B14773BCA97B43A23FB801676BD207A436C6481F1D2B9078717461A5B9D32E688F87748544523B524B0D57D5EA77A2775D2ECFA032CFBDBF52FB3786160279004E57AE6AF874E7303CE53299CCC041C7BC308D82A5698F3A8D0C38271AE35F8E9DBFBB694B5C803D89F7AE435DE236D525F54759B65E372FCD68EF20FA7111F9E4AFF73";
    private static readonly BigInteger N = Integer(Convert.FromHexString(ModulusHex));
    private static readonly BigInteger K = Integer(Hash(Pad(N, Width), Pad(2, Width)));
    public string Username { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AccountName.ToUpperInvariant())));
    public byte[] Salt { get; }
    public string PublicB => Hex(publicB);
    private BigInteger secretB, verifier, publicB;
    private bool used;

    public DemoSrpSession() : this(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(Width)) { }
    // Deterministic inputs are for offline transcript fixtures only.
    internal DemoSrpSession(byte[] salt, byte[] privateB)
    {
        if (salt.Length != 32 || privateB.Length != Width) throw new ArgumentException("Invalid SRP fixture widths.");
        Salt = salt.ToArray();
        secretB = Integer(privateB) % (N - 1);
        if (secretB.IsZero) secretB = 1;
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Username + ":" + Password), Salt, Iterations, HashAlgorithmName.SHA512, 64);
        var signed = new BigInteger(derived, isUnsigned: false, isBigEndian: true);
        var x = ((signed % (N - 1)) + (N - 1)) % (N - 1);
        verifier = BigInteger.ModPow(2, x, N);
        publicB = (K * verifier + BigInteger.ModPow(2, secretB, N)) % N;
        CryptographicOperations.ZeroMemory(derived);
    }
    public string Verify(string publicAHex, string clientM1Hex)
    {
        if (used) return null;
        used = true; // A challenge is consumable exactly once, including a bad proof.
        try {
            var a = Parse(publicAHex, Width);
            var claimed = Parse(clientM1Hex, 32);
            if (a <= 0 || a >= N) return null;
            var u = Integer(Hash(Pad(a, Width), Pad(publicB, Width)));
            if (u.IsZero) return null;
            var shared = BigInteger.ModPow((a * BigInteger.ModPow(verifier, u, N)) % N, secretB, N);
            var expected = Hash(Evidence(a), Evidence(publicB), Evidence(shared));
            if (!CryptographicOperations.FixedTimeEquals(expected, Pad(claimed, 32))) return null;
            return Hex(Integer(Hash(Evidence(a), Evidence(claimed), Evidence(shared))));
        } catch (FormatException) { return null; }
    }
    private static BigInteger Parse(string hex, int maxBytes)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length > maxBytes * 2 || hex.Any(ch => !Uri.IsHexDigit(ch))) throw new FormatException("Invalid bounded SRP integer.");
        if ((hex.Length & 1) != 0) hex = "0" + hex;
        return Integer(Convert.FromHexString(hex));
    }
    private static BigInteger Integer(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);
    private static byte[] Pad(BigInteger value, int count)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length > count) throw new FormatException("SRP integer exceeds width.");
        var output = new byte[count]; bytes.CopyTo(output, count - bytes.Length); return output;
    }
    // The client uses Java-style signed-positive padding, including an extra
    // leading zero whenever the positive integer's bit length is divisible by 8.
    private static byte[] Evidence(BigInteger value) => Pad(value, checked((int)((value.GetBitLength() + 8) / 8)));
    private static byte[] Hash(params byte[][] parts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in parts) hash.AppendData(part);
        return hash.GetHashAndReset();
    }
    private static string Hex(BigInteger value) => Convert.ToHexString(value.ToByteArray(isUnsigned: true, isBigEndian: true));
    public void Dispose() { used = true; secretB = verifier = publicB = BigInteger.Zero; }
}
