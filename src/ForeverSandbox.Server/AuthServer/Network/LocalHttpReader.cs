using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AuthServer.Network;

internal sealed class LocalHttpRequest
{
    public string Method, Path, Body;
    public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
    public int Bytes;
}
internal static class LocalHttpReader
{
    public const int MaxHeaderBytes = 16384, MaxBodyBytes = 65536;
    public static LocalHttpRequest Read(Stream stream)
    {
        using var header = new MemoryStream();
        int matched = 0;
        byte[] end = { 13, 10, 13, 10 };
        while (matched != end.Length) {
            var next = stream.ReadByte();
            if (next < 0) { if (header.Length == 0) return null; throw new IOException("Truncated HTTP header."); }
            if (header.Length >= MaxHeaderBytes) throw new FormatException("HTTP header exceeds limit.");
            header.WriteByte((byte)next);
            matched = next == end[matched] ? matched + 1 : next == 13 ? 1 : 0;
        }
        var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] != "HTTP/1.1" || !first[1].StartsWith('/')) throw new FormatException("Unsupported HTTP request line.");
        var request = new LocalHttpRequest { Method = first[0], Path = first[1].Split('?')[0] };
        for (var i = 1; i < lines.Length && lines[i].Length != 0; ++i) {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0 || !request.Headers.TryAdd(lines[i][..colon].Trim(), lines[i][(colon+1)..].Trim())) throw new FormatException("Invalid or duplicate HTTP header.");
        }
        if (request.Headers.ContainsKey("Transfer-Encoding")) throw new FormatException("Transfer-Encoding is unsupported.");
        var size = 0;
        if (request.Headers.TryGetValue("Content-Length", out var length) && (!int.TryParse(length, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out size) || size < 0 || size > MaxBodyBytes))
            throw new FormatException("Invalid bounded HTTP content length.");
        var body = new byte[size];
        for (int offset = 0; offset < size;) {
            var read = stream.Read(body, offset, size-offset);
            if (read == 0) throw new IOException("Truncated HTTP body.");
            offset += read;
        }
        request.Body = new UTF8Encoding(false, true).GetString(body);
        request.Bytes = checked((int)header.Length + size);
        return request;
    }
}
