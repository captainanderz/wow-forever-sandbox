// Independent, bounded wire codec for the observed Forever authentication-v2 API.
// Field/hash evidence: pinned 5a125dfe Client/api/client/v2/*.pb.h and
// Client/api/common/v2/game_account_handle.pb.h. No generated GPL code imported.
using System;
using System.IO;
using Google.Protobuf;

namespace AuthServer.Network;

internal static class BgsAuthenticationV2
{
    public const uint ServiceHash = 0xC02F8216, ListenerHash = 0x9DA8116B;
    public const uint WowTitleId = 0x576F57;
    public const int MaxRequestBytes = 16384, MaxTokenBytes = 4096;
    public sealed class Logon
    {
        public uint TitleId, Build;
        public string Platform = "", Locale = "";
        public ByteString AuthToken = ByteString.Empty;
    }
    private static CodedInputStream Input(byte[] bytes)
    {
        if (bytes == null || bytes.Length > MaxRequestBytes) throw new FormatException("Authentication request exceeds limit.");
        return new CodedInputStream(bytes);
    }
    public static Logon ReadLogon(byte[] bytes)
    {
        var result = new Logon();
        var input = Input(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
            switch (tag)
            {
                case 8: result.TitleId = input.ReadUInt32(); break;
                case 18: result.Platform = input.ReadString(); break;
                case 26: result.Locale = input.ReadString(); break;
                case 32: result.Build = input.ReadUInt32(); break;
                case 82: result.AuthToken = ReadToken(input.ReadBytes().ToByteArray()); break;
                default: input.SkipLastField(); break;
            }
        if (result.Platform.Length > 32 || result.Locale.Length > 16) throw new FormatException("Authentication metadata exceeds limit.");
        return result;
    }
    // Both LogonOptions.auth_token and VerifyAuthTokenRequest.auth_token are field 1.
    public static ByteString ReadToken(byte[] bytes)
    {
        var result = ByteString.Empty;
        var input = Input(bytes);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (tag == 10) result = input.ReadBytes();
            else input.SkipLastField();
        }
        if (result.Length > MaxTokenBytes) throw new FormatException("Authentication token exceeds limit.");
        return result;
    }
    private static byte[] Encode(Action<CodedOutputStream> write)
    {
        using var memory = new MemoryStream();
        using (var output = new CodedOutputStream(memory, true)) { write(output); output.Flush(); }
        return memory.ToArray();
    }
    private static void Bytes(CodedOutputStream output, int field, byte[] bytes)
    {
        output.WriteTag(field, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(bytes));
    }
    private static void UInt(CodedOutputStream output, int field, ulong value)
    {
        output.WriteTag(field, WireFormat.WireType.Varint);
        output.WriteUInt64(value);
    }
    public static byte[] Challenge(string host, int port)
    {
        if (host != "localhost" && host != "127.0.0.1" && host != "localhost.actual.battle.net")
            throw new ArgumentException("Only the configured local test hosts are allowed.");
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return Encode(output => {
            output.WriteTag(2, WireFormat.WireType.LengthDelimited); output.WriteString("web_auth_url");
            Bytes(output, 3, System.Text.Encoding.UTF8.GetBytes($"https://{host}:{port}/login/?app=wow"));
        });
    }
    public static byte[] Complete(byte[] sessionKey)
    {
        if (sessionKey == null || sessionKey.Length != 64) throw new ArgumentException("Expected 64 raw session-key bytes.");
        var account = Encode(output => { UInt(output, 1, 1); UInt(output, 2, WowTitleId); UInt(output, 3, 2); });
        var record = Encode(output => {
            UInt(output, 1, 1); Bytes(output, 2, account);
            output.WriteTag(3, WireFormat.WireType.LengthDelimited); output.WriteString("Sandbox#0001");
            Bytes(output, 5, sessionKey);
        });
        return Encode(output => { UInt(output, 1, 0); Bytes(output, 2, record); });
    }
    public static byte[] TokenResponse(byte[] token)
    {
        if (token == null || token.Length > MaxTokenBytes) throw new ArgumentException("Invalid token length.");
        return Encode(output => Bytes(output, 1, token));
    }
}
