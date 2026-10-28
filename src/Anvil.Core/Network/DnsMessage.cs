using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Anvil.Core.Network;

public enum DnsReplyCode : byte { NoError = 0, Refused = 5 }

/// <summary>A DNS query's one question. Name is lower case without a trailing dot; RawQuestion is the question section as sent.</summary>
public sealed record DnsQuestion(ushort Id, bool RecursionDesired, string Name, ushort Type, ushort Class, ReadOnlyMemory<byte> RawQuestion);

/// <summary>
/// Just enough of RFC 1035 to answer one-question queries over UDP: anything else is not parsed, and the responder sends
/// nothing back for it.
/// </summary>
public static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;
    public const ushort ClassIn = 1;

    private const int HeaderLength = 12;
    private const int MaxLabel = 63;
    private const int MaxName = 255;

    /// <returns>The question of a standard query with exactly one, or null for anything else.</returns>
    public static DnsQuestion? TryParse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderLength) return null;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        var isResponse = (flags & 0x8000) != 0;
        var opcode = (flags >> 11) & 0x0F;
        if (isResponse || opcode != 0 || BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) != 1) return null;

        var name = new StringBuilder();
        var at = HeaderLength;
        var nameLength = 0;
        while (true)
        {
            if (at >= packet.Length) return null;
            int length = packet[at];
            nameLength += length + 1;
            if (nameLength > MaxName) return null;
            if (length == 0) break;
            // 0xC0 and above is a compression pointer, which a question never needs; 64 to 191 are not label lengths.
            if (length > MaxLabel || at + 1 + length > packet.Length) return null;
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(packet.Slice(at + 1, length)).ToLowerInvariant());
            at += 1 + length;
        }

        at++; // the root label
        if (at + 4 > packet.Length) return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
        var cls = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
        at += 4;
        return new DnsQuestion(BinaryPrimitives.ReadUInt16BigEndian(packet), (flags & 0x0100) != 0, name.ToString(), type, cls,
            packet[HeaderLength..at].ToArray());
    }

    /// <summary>An authoritative reply to <paramref name="q"/>: one A record for <paramref name="address"/>, or no answer.</summary>
    public static byte[] Reply(DnsQuestion q, DnsReplyCode code, IPAddress? address, uint ttl)
    {
        var answer = address is null ? 0 : 16;
        var reply = new byte[HeaderLength + q.RawQuestion.Length + answer];
        var span = reply.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, q.Id);
        BinaryPrimitives.WriteUInt16BigEndian(span[2..], (ushort)(0x8000 | 0x0400 | (q.RecursionDesired ? 0x0100 : 0) | (byte)code));
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..], (ushort)(address is null ? 0 : 1));
        q.RawQuestion.Span.CopyTo(span[HeaderLength..]);
        if (address is null) return reply;

        var record = span[(HeaderLength + q.RawQuestion.Length)..];
        BinaryPrimitives.WriteUInt16BigEndian(record, 0xC00C); // the name: a pointer to the question's
        BinaryPrimitives.WriteUInt16BigEndian(record[2..], TypeA);
        BinaryPrimitives.WriteUInt16BigEndian(record[4..], ClassIn);
        BinaryPrimitives.WriteUInt32BigEndian(record[6..], ttl);
        BinaryPrimitives.WriteUInt16BigEndian(record[10..], 4);
        if (!address.TryWriteBytes(record[12..], out var written) || written != 4)
            throw new ArgumentException("An A record needs an IPv4 address.", nameof(address));
        return reply;
    }
}
