// Current Connect wire-field semantics; no upstream implementation code copied.
// Reference: TrinityCore 7516455b (ConnectionService.cpp, BattlenetRpcErrorCodes.h).
using System;
using System.IO;
using Bgs.Protocol;
using Bgs.Protocol.Connection.V1;
using Google.Protobuf;

namespace AuthServer.Network;

internal static class BgsConnectionHandshake
{
    public static byte[] Build(ConnectRequest request, uint clientLabel, uint serverLabel, DateTimeOffset now)
    {
        if (clientLabel == 0 || serverLabel == 0) throw new ArgumentException("Connection identities must be nonzero.");
        var epoch = checked((uint)now.ToUnixTimeSeconds());
        var reply = new ConnectResponse
        {
            ServerId = new ProcessId { Label = serverLabel, Epoch = epoch },
            ClientId = request.ClientId?.Clone() ?? new ProcessId { Label = clientLabel, Epoch = epoch },
            ServerTime = checked((ulong)now.ToUnixTimeMilliseconds()),
            UseBindlessRpc = request.UseBindlessRpc
        };
        // Field9 is the current string CIID. Keep the existing generated schema
        // intact and append this explicitly verified extension on the wire.
        var ciid = $"{reply.ServerId.Label:X8}{reply.ServerId.Epoch:X8}-{reply.ClientId.Label:X8}{reply.ClientId.Epoch:X8}";
        using var memory = new MemoryStream();
        using (var output = new CodedOutputStream(memory))
        {
            reply.WriteTo(output);
            output.WriteTag(9, WireFormat.WireType.LengthDelimited);
            output.WriteString(ciid);
        }
        return memory.ToArray();
    }
}

internal static class BgsFrameWriter
{
    public static byte[] Reply(uint requestToken, byte[] payload, uint status = 0)
    {
        if (payload == null || payload.Length > BgsFrameReader.MaxPayloadBytes)
            throw new ArgumentException("Invalid BGS reply payload length.");
        var header = new Header { ServiceId = 0xFE, Token = requestToken, Size = (uint)payload.Length, Status = status }.ToByteArray();
        return Pack(header, payload);
    }
    public static byte[] Notification(uint token, uint serviceHash, uint method, byte[] payload)
    {
        if (payload == null || payload.Length > BgsFrameReader.MaxPayloadBytes) throw new ArgumentException("Invalid BGS notification payload length.");
        return Pack(new Header { ServiceId = 0, ServiceHash = serviceHash, MethodId = method, Token = token, Size = (uint)payload.Length }.ToByteArray(), payload);
    }
    private static byte[] Pack(byte[] header, byte[] payload)
    {
        if (header.Length > BgsFrameReader.MaxHeaderBytes) throw new InvalidOperationException("BGS reply header exceeds bound.");
        var frame = new byte[2 + header.Length + payload.Length];
        frame[0] = (byte)(header.Length >> 8);
        frame[1] = (byte)header.Length;
        Buffer.BlockCopy(header, 0, frame, 2, header.Length);
        Buffer.BlockCopy(payload, 0, frame, 2 + header.Length, payload.Length);
        return frame;
    }
}
