using System.Text;

namespace Anvil.Core.Tests.Network;

/// <summary>Builds DNS query packets the way a resolver sends them.</summary>
internal static class DnsPackets
{
    public static byte[] Query(ushort id, string name, ushort type, bool edns = false, bool recursionDesired = true)
    {
        var bytes = new List<byte> { (byte)(id >> 8), (byte)id, (byte)(recursionDesired ? 0x01 : 0x00), 0x00, 0, 1, 0, 0, 0, 0, 0, (byte)(edns ? 1 : 0) };
        foreach (var label in name.Split('.'))
        {
            var text = Encoding.ASCII.GetBytes(label);
            bytes.Add((byte)text.Length);
            bytes.AddRange(text);
        }

        bytes.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        // An OPT record: root name, type 41, a 4096-byte UDP size, no options.
        if (edns) bytes.AddRange([0, 0, 41, 0x10, 0x00, 0, 0, 0, 0, 0, 0]);
        return [.. bytes];
    }

    public static byte[] With(byte[] packet, int index, byte value)
    {
        var copy = (byte[])packet.Clone();
        copy[index] = value;
        return copy;
    }
}
