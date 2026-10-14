// Independent 70338 Battle.net-over-world relay. After the world login the client
// drops its direct BGS connection and sends realm/ruleset RPCs through the world
// socket (character create waits for one of them). Factual layouts: pinned 5a125dfe
// BattlenetPackets.cpp, BattlenetHandler.cpp, WorldSession.cpp. No reference code imported.
using System;
using System.Buffers.Binary;
using System.Text;

namespace AuthServer.Network;

internal static class ForeverBattlenetRelay
{
    public const uint RequestOpcode = 0x440124, ChangeRealmTicketOpcode = 0x440129;
    public const uint ResponseOpcode = 0x4702B0, ConnectionStatusOpcode = 0x4702B2, ChangeRealmTicketResponseOpcode = 0x4702B3;
    public const uint NotImplemented = 0xBC7;
    private const uint FriendsServiceHash = 0x5869BE8C, BlockListServiceHash = 0x8E8F5FB0, SubscribeMethod = 0x40000001;
    // Resources GetContentHandle (profanity list 'apft'): declined. The official handle makes the client fetch the file
    // from Blizzard's depot (a failed fetch can hang 'Retrieving character list'); the error only disables profanity filtering.
    public const uint ResourcesServiceHash = 0xECBE75BA;

    public sealed record Request(ulong Type, ulong ObjectId, uint Token, byte[] Data)
    {
        public uint ServiceHash => (uint)(Type >> 32);
        public uint MethodId => (uint)Type & 0x3FFFFFFF;
    }

    public static Request ReadRequest(byte[] payload)
    {
        if (payload.Length < 24) throw new FormatException("Truncated Battle.net request.");
        var size = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(20));
        if (size > 0xFFFF || size != payload.Length - 24) throw new FormatException("Invalid Battle.net request size.");
        return new Request(BinaryPrimitives.ReadUInt64LittleEndian(payload), BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16)), payload[24..]);
    }

    public static byte[] Response(uint status, ulong type, ulong objectId, uint token, byte[] data) => new ForeverPacketWriter()
        .U32(status).U64(type).U64(objectId).U32(token).U32((uint)data.Length).Raw(data).ToArray();

    // State 1 = connected; notifications not suppressed (the official server sends 0x40).
    public static byte[] ConnectionStatus() => new ForeverPacketWriter().Bits(1, 2).Bit(false).ToArray();

    // The official server then sends unasked friends-v2 and block-list Subscribe results (token 0, object 0).
    public static (uint opcode, byte[] payload)[] SubscribeResults() => new[] {
        (ResponseOpcode, Response(0, (ulong)FriendsServiceHash << 32 | SubscribeMethod, 0, 0, Array.Empty<byte>())),
        (ResponseOpcode, Response(0, (ulong)BlockListServiceHash << 32 | SubscribeMethod, 0, 0, new byte[] { 0x0A, 0x00 })) };

    public static (uint token, byte[] secret) ReadChangeRealmTicket(byte[] payload)
    {
        if (payload.Length != 36) throw new FormatException("Invalid change-realm-ticket size.");
        return (BinaryPrimitives.ReadUInt32LittleEndian(payload), payload[4..]);
    }

    public static byte[] ChangeRealmTicketResponse(uint token) => new ForeverPacketWriter()
        .U32(token).Bit(true).U32(27).Raw(Encoding.ASCII.GetBytes("WorldserverRealmListTicket\0")).ToArray();

    // Realm/ruleset RPCs reuse the BGS realm codec with this world session's realm-list state.
    public static (uint status, byte[] payload, string route) Dispatch(Request request, BgsRealmState state, int realmPort)
    {
        if (request.ServiceHash is BgsRealmV2.ServiceHash or BgsRealmV2.NameHash && BgsRealmV2.Handles(request.MethodId))
            return BgsRealmV2.Reply(request.MethodId, request.Data, state, true, realmPort);
        if (request.ServiceHash == ResourcesServiceHash)
            return (NotImplemented, Array.Empty<byte>(), "content_handle_declined");
        // friends v2 GetSentInvitations (3), GetReceivedInvitations (4), GetFriends (5): an empty sandbox list.
        if (request.ServiceHash == FriendsServiceHash && request.MethodId is 3 or 4 or 5)
            return (0, Array.Empty<byte>(), "friends_empty");
        return (NotImplemented, Array.Empty<byte>(), "unsupported_service");
    }
}
