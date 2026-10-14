// Independent 70338 pre-encryption world framing and authentication shape.
// Contract: pinned ClassicOpcodes, WorldSocket and AuthenticationPackets references.
using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace AuthServer.Network;

internal sealed record ForeverWorldFrame(uint Opcode, byte[] Payload);
internal sealed record ForeverAuthShape(uint Region, uint Site, uint Realm, bool UseIpv6, int TicketBytes);
internal sealed record ForeverAuthSession(ForeverAuthShape Shape, byte[] LocalChallenge, byte[] Digest, string GameAccount, uint Platform, uint Type, uint Arch);
internal static class ForeverWorldWire
{
    public const uint AuthChallengeOpcode=0x4E0000, AuthSessionOpcode=0x460001;
    public const int MaxPayloadBytes=65536, MaxIncomingBytes=0x100000;
    // Every frame passes through the connection's crypt so both counters advance
    // from the first packet; before Enable the tag is zero and data is clear.
    public static byte[] Seal(ForeverPacketCrypt crypt,uint opcode,byte[] payload) {
        if(payload==null || payload.Length>MaxIncomingBytes)throw new ArgumentException("World payload exceeds limit.");
        var plain=new byte[4+payload.Length];BinaryPrimitives.WriteUInt32LittleEndian(plain,opcode);payload.CopyTo(plain,4);
        var wire=new byte[16+plain.Length];var cipher=new byte[plain.Length];var tag=new byte[12];
        crypt.Encrypt(plain,cipher,tag);
        BinaryPrimitives.WriteUInt32LittleEndian(wire,(uint)plain.Length);tag.CopyTo(wire,4);cipher.CopyTo(wire,16);return wire;
    }
    public static ForeverWorldFrame ReadSealed(Stream stream,ForeverPacketCrypt crypt) {
        var header=new byte[16];var first=stream.ReadByte();if(first<0)return null;header[0]=(byte)first;
        Exact(stream,header,1,15);
        var size=BinaryPrimitives.ReadUInt32LittleEndian(header);
        if(size<4 || size>MaxIncomingBytes)throw new FormatException("Invalid bounded world frame size.");
        if(!crypt.Enabled && header.Skip(4).Any(value=>value!=0))throw new FormatException("Unexpected pre-encryption world tag.");
        var body=new byte[(int)size];Exact(stream,body,0,body.Length);
        var plain=new byte[body.Length];crypt.Decrypt(body,header[4..],plain);
        return new ForeverWorldFrame(BinaryPrimitives.ReadUInt32LittleEndian(plain),plain[4..]);
    }
    public static ForeverAuthSession AuthSession(byte[] payload) {
        var shape=AuthShape(payload);
        var ticket=JsonDocument.Parse(Encoding.UTF8.GetString(payload,81,shape.TicketBytes));
        using(ticket){var root=ticket.RootElement;return new ForeverAuthSession(shape,payload[20..52],payload[52..76],root.GetProperty("gameAccount").GetString(),
            root.GetProperty("platform").GetUInt32(),root.GetProperty("type").GetUInt32(),root.GetProperty("clientArch").GetUInt32());}
    }
    public const string ServerInitializer="WORLD OF WARCRAFT CONNECTION - SERVER TO CLIENT - V2\n";
    public const string ClientInitializer="WORLD OF WARCRAFT CONNECTION - CLIENT TO SERVER - V2\n";
    public static string ReadInitializer(Stream stream) {
        using var bytes=new MemoryStream();
        while(bytes.Length<128){var next=stream.ReadByte();if(next<0)throw new IOException("Truncated world initializer.");bytes.WriteByte((byte)next);if(next==10)return Encoding.ASCII.GetString(bytes.ToArray());}
        throw new FormatException("World initializer exceeds limit.");
    }
    public static byte[] Pack(uint opcode,byte[] payload) {
        if(payload==null || payload.Length>MaxPayloadBytes)throw new ArgumentException("World payload exceeds limit.");
        var wire=new byte[20+payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(wire,(uint)payload.Length+4);
        // Bytes 4..15 are the 12-byte authentication tag; zero before encryption.
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(16),opcode);
        payload.CopyTo(wire,20);return wire;
    }
    public static ForeverWorldFrame Read(Stream stream) {
        var header=new byte[16];var first=stream.ReadByte();if(first<0)return null;header[0]=(byte)first;
        Exact(stream,header,1,15);
        var size=BinaryPrimitives.ReadUInt32LittleEndian(header);
        if(size<4 || size>MaxPayloadBytes+4)throw new FormatException("Invalid bounded world frame size.");
        if(header.Skip(4).Any(value=>value!=0))throw new FormatException("Unexpected pre-encryption world tag.");
        var body=new byte[(int)size];Exact(stream,body,0,body.Length);
        return new ForeverWorldFrame(BinaryPrimitives.ReadUInt32LittleEndian(body),body[4..]);
    }
    public static byte[] Challenge(byte[] dos,byte[] serverChallenge) {
        if(dos.Length!=32 || serverChallenge.Length!=32)throw new ArgumentException("World challenge fields require 32 bytes each.");
        return Pack(AuthChallengeOpcode,dos.Concat(serverChallenge).Concat(new byte[]{1}).ToArray());
    }
    public static ForeverAuthShape AuthShape(byte[] payload) {
        // uint64 DOS, three uint32 IDs, 32-byte local challenge, 24-byte digest,
        // one flushed bit byte, uint32 length, raw UTF8 realm-join JSON.
        if(payload.Length<81)throw new FormatException("Truncated world auth session.");
        var length=BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(77));
        if(length==0 || length>8192 || length!=payload.Length-81)throw new FormatException("Invalid world auth ticket length.");
        var ticket=new UTF8Encoding(false,true).GetString(payload,81,(int)length);
        using var document=JsonDocument.Parse(ticket,new JsonDocumentOptions{MaxDepth=8});
        var root=document.RootElement;
        var name=root.GetProperty("gameAccount").GetString();
        if(string.IsNullOrEmpty(name) || name.Length>128)throw new FormatException("Invalid world demo ticket shape.");
        root.GetProperty("platform").GetUInt32();root.GetProperty("type").GetUInt32();root.GetProperty("clientArch").GetUInt32();
        return new ForeverAuthShape(BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12)),BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16)),
            (payload[76]&0x80)!=0,(int)length);
    }
    private static void Exact(Stream stream,byte[] bytes,int offset,int count){var end=offset+count;while(offset<end){var read=stream.Read(bytes,offset,end-offset);if(read==0)throw new IOException("Truncated world frame.");offset+=read;}}
}
