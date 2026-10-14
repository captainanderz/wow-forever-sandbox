// Independent demo account-v2 wire codec. Field/method evidence is in pinned
// 5a125dfe api/client/v2/account_service and api/common/v2/account_types headers.
using System;
using System.IO;
using Google.Protobuf;

namespace AuthServer.Network;

internal static class BgsAccountV2
{
    public const uint ServiceHash = 0x22DC2464;
    public const int MaxRequestBytes = 16384;
    public static bool Handles(uint method) => method is 101 or 104 or 201 or 203;
    public static (uint status, byte[] payload) Reply(uint method, byte[] payload, bool authenticated)
    {
        if (!authenticated || !Handles(method)) return (3, Array.Empty<byte>());
        if (payload == null || payload.Length > MaxRequestBytes) throw new FormatException("Account request exceeds limit.");
        var request = new CodedInputStream(payload);
        ulong id = 0;
        uint title = 0, region = 0, tag;
        bool hasHandle = false;
        while ((tag = request.ReadTag()) != 0) {
            if (tag == 10 && method is 201 or 203) {
                var handleBytes = request.ReadBytes();
                if (hasHandle || handleBytes.Length > 256) throw new FormatException("Invalid bounded game-account handle.");
                hasHandle = true;
                var handle = new CodedInputStream(handleBytes.ToByteArray());
                uint field;
                while ((field = handle.ReadTag()) != 0) {
                    switch (field) {
                        case 8: id = handle.ReadUInt64(); break;
                        case 16: title = handle.ReadUInt32(); break;
                        case 24: region = handle.ReadUInt32(); break;
                        default: handle.SkipLastField(); break;
                    }
                }
            } else request.SkipLastField();
        }
        if (method is 201 or 203 && (!hasHandle || id != 1 || title != BgsAuthenticationV2.WowTitleId || region != 2))
            return (3, Array.Empty<byte>());
        if (method is 104 or 203) return (0, Array.Empty<byte>()); // No local demo restrictions.
        var info = Pack(output => {
            UInt64(output, 1, 1);
            if (method == 201) { Text(output, 2, "WoW1"); return; }
            // Synthetic local demo metadata, matching the reference's catalog defaults.
            UInt32(output, 2, 1);
            Text(output, 5, "Sandbox#0001");
            Text(output, 7, "USA"); UInt32(output, 8, 1);
            Text(output, 9, "enUS"); Text(output, 10, "USD");
            UInt32(output, 11, 1); UInt32(output, 14, 7); // Hidden from friend finder.
        });
        return (0, Pack(output => { output.WriteTag(1, WireFormat.WireType.LengthDelimited); output.WriteBytes(ByteString.CopyFrom(info)); }));
    }
    private static byte[] Pack(Action<CodedOutputStream> fill)
    {
        using var memory = new MemoryStream();
        using (var output = new CodedOutputStream(memory, true)) { fill(output); output.Flush(); }
        return memory.ToArray();
    }
    private static void UInt32(CodedOutputStream output, int field, uint value) { output.WriteTag(field, WireFormat.WireType.Varint); output.WriteUInt32(value); }
    private static void UInt64(CodedOutputStream output, int field, ulong value) { output.WriteTag(field, WireFormat.WireType.Varint); output.WriteUInt64(value); }
    private static void Text(CodedOutputStream output, int field, string value) { output.WriteTag(field, WireFormat.WireType.LengthDelimited); output.WriteString(value); }
}
