// Independent 70338 world session keys, EnterEncryptedMode signature and AES-GCM
// framing. Factual constants/layouts: pinned 5a125dfe WorldSocket.cpp,
// AuthenticationPackets.cpp, WorldPacketCrypt.cpp and SessionKeyGenerator.h.
// No reference code imported. Secrets are never logged.
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

namespace AuthServer.Network;

internal static class ForeverWorldKeys
{
    private static readonly byte[] SessionKeySeed = { 0xE8, 0x1E, 0x8B, 0x59, 0x27, 0x62, 0x1E, 0xAA, 0x86, 0x15, 0x18, 0xEA, 0xC0, 0xBF, 0x66, 0x8C,
        0x6D, 0xBF, 0x83, 0x93, 0xBC, 0xAA, 0x80, 0x52, 0x5B, 0x1E, 0xDC, 0x23, 0xA0, 0x12, 0xB7, 0x50 };
    private static readonly byte[] EncryptionKeySeed = { 0x71, 0xC9, 0xED, 0x5A, 0xA7, 0x0E, 0x4D, 0xFF, 0x4C, 0x36, 0xA6, 0x5A, 0x3E, 0x46, 0x8A, 0x4A,
        0x5D, 0xA1, 0x48, 0xC8, 0x30, 0x47, 0x4A, 0xDE, 0xF6, 0x0D, 0x6C, 0xBE, 0x6F, 0xE4, 0x55, 0x73 };
    private static readonly byte[] EnableEncryptionSeed = { 0x66, 0xBE, 0x29, 0x79, 0xEF, 0xF2, 0xD5, 0xB5, 0x61, 0x53, 0xF6, 0x5F, 0x45, 0xAE, 0x81, 0xCB,
        0x32, 0xEC, 0x94, 0xEC, 0x75, 0xB3, 0x5F, 0x44, 0x6A, 0x63, 0x43, 0x67, 0x17, 0x20, 0x44, 0x34 };
    internal static readonly byte[] EnableEncryptionContext = { 0xA7, 0x1F, 0xB6, 0x9B, 0xC9, 0x7C, 0xDD, 0x96, 0xE9, 0xBB, 0xB8, 0x21, 0x39, 0x8D, 0x5A, 0xD4 };
    // Published TrinityCore EnterEncryptedMode signing seed (interop constant, not a
    // secret). Its public half is ForeverLauncher's default region-group-8 store key
    // (RealmKeys.cs in the launcher); the client trusts it only after that write.
    internal static readonly byte[] InteropSigningSeed = { 0x08, 0xBD, 0xC7, 0xA3, 0xCC, 0xC3, 0x4F, 0x3F, 0x6A, 0x0B, 0xFF, 0xCF, 0x31, 0xC1, 0xB6, 0x97,
        0x69, 0x1E, 0x72, 0x9A, 0x0A, 0xAB, 0x2C, 0x77, 0xC3, 0x6F, 0x8A, 0xE7, 0x5A, 0x9A, 0xA7, 0xC9 };
    public const int RegionGroup = 8;

    public static byte[] SessionKey(byte[] keyData, byte[] serverChallenge, byte[] localChallenge)
    {
        Require(keyData, 64, "realm join key data"); Require(serverChallenge, 32, "server challenge"); Require(localChallenge, 32, "local challenge");
        var seed = HMACSHA512.HashData(SHA512.HashData(keyData), Concat(serverChallenge, localChallenge, SessionKeySeed));
        // SessionKeyGenerator<SHA512>: o1/o2 hash the halves; o0 = H(o1 | o0 | o2), starting from zeros.
        var o1 = SHA512.HashData(seed.AsSpan(0, 32)); var o2 = SHA512.HashData(seed.AsSpan(32));
        var o0 = new byte[64]; var key = new byte[40]; var used = o0.Length;
        for (var i = 0; i < key.Length; i++) { if (used == o0.Length) { o0 = SHA512.HashData(Concat(o1, o0, o2)); used = 0; } key[i] = o0[used++]; }
        CryptographicOperations.ZeroMemory(seed);
        return key;
    }

    public static byte[] EncryptKey(byte[] sessionKey, byte[] localChallenge, byte[] serverChallenge)
    {
        Require(sessionKey, 40, "session key");
        var digest = HMACSHA512.HashData(sessionKey, Concat(localChallenge, serverChallenge, EncryptionKeySeed));
        var key = digest[..32]; CryptographicOperations.ZeroMemory(digest); return key;
    }

    public static byte[] EnterEncryptedMode(byte[] encryptKey, byte[] signingSeed, int regionGroup = RegionGroup, bool enabled = true)
    {
        Require(encryptKey, 32, "encryption key");
        var signature = Ed25519Ctx.Sign(signingSeed, SignedDigest(encryptKey, enabled), EnableEncryptionContext);
        var payload = new byte[69];
        BinaryPrimitives.WriteInt32LittleEndian(payload, regionGroup); signature.CopyTo(payload, 4);
        payload[68] = enabled ? (byte)0x80 : (byte)0;
        return payload;
    }

    internal static byte[] SignedDigest(byte[] encryptKey, bool enabled) => HMACSHA512.HashData(encryptKey, Concat(new[] { enabled ? (byte)1 : (byte)0 }, EnableEncryptionSeed));

    private static readonly byte[] ContinuedSessionSeed = { 0x56, 0x5C, 0x61, 0x9C, 0x48, 0x3A, 0x52, 0x1F, 0x61, 0x5D, 0x05, 0x49, 0xB2, 0x9A, 0x39, 0xBF,
        0x4B, 0x97, 0xB0, 0x1B, 0xF9, 0x6C, 0xDE, 0xD6, 0x80, 0x1D, 0xAB, 0x26, 0x02, 0xA9, 0x9B, 0x9D };

    // CMSG_AUTH_CONTINUED_SESSION proof: HMAC-SHA512(session key, key | local | server | seed), first 24 bytes.
    public static byte[] ContinuedSessionDigest(byte[] sessionKey, ulong connectKey, byte[] localChallenge, byte[] serverChallenge)
    {
        Require(sessionKey, 40, "session key"); Require(localChallenge, 32, "local challenge"); Require(serverChallenge, 32, "server challenge");
        var key = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(key, connectKey);
        var digest = HMACSHA512.HashData(sessionKey, Concat(key, localChallenge, serverChallenge, ContinuedSessionSeed));
        var proof = digest[..24]; CryptographicOperations.ZeroMemory(digest); return proof;
    }

    private static void Require(byte[] value, int length, string name) { if (value?.Length != length) throw new ArgumentException($"The {name} requires {length} bytes."); }
    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();
}

// Counters advance for every frame from connection start, encrypted or not; the
// 12-byte IV is uint64 counter + uint32 direction magic, tags are 12 bytes.
internal sealed class ForeverPacketCrypt : IDisposable
{
    private const uint ClientMagic = 0x544E4C43, ServerMagic = 0x52565253;
    private AesGcm aes;
    private ulong clientCounter, serverCounter;
    public bool Enabled => aes != null;
    public void Enable(byte[] key) { if (key?.Length != 32 || aes != null) throw new InvalidOperationException("World encryption requires one 32-byte key."); aes = new AesGcm(key, 12); }
    // Locked: the realm connection's reader thread decrypts while the instance socket may send login packets on it.
    public void Encrypt(byte[] plain, byte[] cipher, byte[] tag) { lock (this) { var iv = Iv(serverCounter++, ServerMagic); if (aes == null) { plain.CopyTo(cipher, 0); Array.Clear(tag); } else aes.Encrypt(iv, plain, cipher, tag); } }
    public void Decrypt(byte[] cipher, byte[] tag, byte[] plain) { lock (this) { var iv = Iv(clientCounter++, ClientMagic); if (aes == null) cipher.CopyTo(plain, 0); else aes.Decrypt(iv, cipher, tag, plain); } }
    public void Dispose() { aes?.Dispose(); aes = null; }
    private static byte[] Iv(ulong counter, uint magic) { var iv = new byte[12]; BinaryPrimitives.WriteUInt64LittleEndian(iv, counter); BinaryPrimitives.WriteUInt32LittleEndian(iv.AsSpan(8), magic); return iv; }
}

// Realm join key data (client secret 32 + server secret 32) handed from the BGS
// session to the world listener in this one process. Latest join per game account
// wins; a ruleset switch rejoins through the world server and reuses it.
internal static class ForeverJoinRegistry
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<string, (byte[] keyData, DateTime expires)> joins = new(StringComparer.Ordinal);
    public static void Publish(string gameAccount, byte[] keyData)
    {
        if (string.IsNullOrEmpty(gameAccount) || keyData?.Length != 64) throw new ArgumentException("Invalid realm join key data.");
        joins.AddOrUpdate(gameAccount, (keyData.ToArray(), DateTime.UtcNow + Lifetime), (_, old) => { CryptographicOperations.ZeroMemory(old.keyData); return (keyData.ToArray(), DateTime.UtcNow + Lifetime); });
    }
    public static byte[] Find(string gameAccount)
    {
        if (gameAccount == null || !joins.TryGetValue(gameAccount, out var join)) return null;
        if (join.expires > DateTime.UtcNow) return join.keyData.ToArray();
        if (joins.TryRemove(new(gameAccount, join))) CryptographicOperations.ZeroMemory(join.keyData);
        return null;
    }
}
