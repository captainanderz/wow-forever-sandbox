// Transport framing only; uses the existing protobuf Header schema.
using System;
using System.IO;
using Bgs.Protocol;
using Google.Protobuf;

namespace AuthServer.Network;

internal sealed class BgsFrame
{
    public Header Header { get; }
    public byte[] Payload { get; }
    public BgsFrame(Header header, byte[] payload) { Header = header; Payload = payload; }
}

internal sealed class BgsFramingException : IOException
{
    public string Stage { get; }
    public string Reason { get; }
    public int ExpectedBytes { get; }
    public int ReadBytes { get; }
    public uint DeclaredBytes { get; }
    public int LimitBytes { get; }

    public BgsFramingException(string stage, string reason, int expectedBytes = 0, int readBytes = 0, uint declaredBytes = 0, int limitBytes = 0)
        : base($"BGS framing failure: {stage}/{reason}; expected={expectedBytes}, read={readBytes}, declared={declaredBytes}, limit={limitBytes}.")
    {
        Stage = stage;
        Reason = reason;
        ExpectedBytes = expectedBytes;
        ReadBytes = readBytes;
        DeclaredBytes = declaredBytes;
        LimitBytes = limitBytes;
    }
}

internal static class BgsFrameReader
{
    public const int MaxHeaderBytes = 4096;
    public const int MaxPayloadBytes = 1024 * 1024;

    // Null means EOF before the first byte of a new frame, a normal close.
    public static BgsFrame ReadNext(Stream stream)
    {
        var prefix = new byte[2];
        if (!Fill(stream, prefix, "prefix", allowInitialEof: true)) return null;
        var headerLength = (prefix[0] << 8) | prefix[1];
        if (headerLength == 0)
            throw new BgsFramingException("header-length", "zero-header-length");
        if (headerLength > MaxHeaderBytes)
            throw new BgsFramingException("header-length", "header-too-large", declaredBytes: (uint)headerLength, limitBytes: MaxHeaderBytes);

        var headerBytes = new byte[headerLength];
        Fill(stream, headerBytes, "header");
        Header header;
        try { header = Header.Parser.ParseFrom(headerBytes); }
        catch (InvalidProtocolBufferException)
        {
            throw new BgsFramingException("protobuf-header", "invalid-protobuf");
        }
        if (header.Size > MaxPayloadBytes)
            throw new BgsFramingException("payload-length", "payload-too-large", declaredBytes: header.Size, limitBytes: MaxPayloadBytes);

        var payload = header.Size == 0 ? Array.Empty<byte>() : new byte[(int)header.Size];
        Fill(stream, payload, "payload");
        return new BgsFrame(header, payload);
    }

    private static bool Fill(Stream stream, byte[] buffer, string stage, bool allowInitialEof = false)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                if (allowInitialEof && offset == 0) return false;
                throw new BgsFramingException(stage, "partial-eof", buffer.Length, offset);
            }
            offset += read;
        }
        return true;
    }
}
