using System.Buffers.Binary;
using System.Text;

namespace SwitchPilot.Core.Discovery;

// Strict bounds checks: packet capture is untrusted input. No packet payload is persisted.
public static class NeighborParser
{
    private static string Text(ReadOnlySpan<byte> data) => new(Encoding.UTF8.GetString(data).Where(c => !char.IsControl(c)).Take(256).ToArray());
    public static NeighborAnnouncement? Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 14) return null;
        var etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[12..]);
        var offset = 14;
        for (var tag = 0; tag < 2 && etherType is 0x8100 or 0x88a8; tag++)
        {
            if (frame.Length < offset + 4) return null;
            etherType = BinaryPrimitives.ReadUInt16BigEndian(frame[(offset + 2)..]); offset += 4;
        }
        if (etherType == 0x88cc) return Lldp(frame[offset..]);
        // IEEE 802.3 LLC/SNAP Cisco OUI 00:00:0c, PID 0x2000.
        if (etherType is >= 12 and <= 1500 && frame.Length >= offset + 12 && frame.Slice(offset, 8).SequenceEqual(new byte[] { 0xaa, 0xaa, 3, 0, 0, 0x0c, 0x20, 0 }))
        {
            if (frame.Length < offset + etherType) return null;
            return Cdp(frame.Slice(offset + 8, etherType - 8));
        }
        return null;
    }
    private static NeighborAnnouncement? Lldp(ReadOnlySpan<byte> data)
    {
        string name = "", chassis = "", port = "";
        int? vlan = null; var ttl = -1; var mandatory = 0;
        while (data.Length >= 2)
        {
            var header = BinaryPrimitives.ReadUInt16BigEndian(data); var type = header >> 9; var length = header & 0x1ff;
            data = data[2..];
            if (length > data.Length) return null;
            var value = data[..length]; data = data[length..];
            if (mandatory < 3 && type != mandatory + 1) return null;
            if (type is >= 1 and <= 3)
            {
                if (mandatory >= 3 || type != mandatory + 1) return null;
                if (type is 1 or 2 && (length < 2 || value[0] is < 1 or > 7)) return null;
                if ((type == 1 && value[0] == 4 || type == 2 && value[0] == 3) && length != 7) return null;
                if (type == 3 && length != 2) return null;
                mandatory++;
            }
            switch (type)
            {
                case 0: return length == 0 && mandatory == 3 && ttl > 0 && chassis.Length > 0 && port.Length > 0 ? new("LLDP", name.Length > 0 ? name : chassis, port, ValidVlan(vlan), ttl, DateTimeOffset.UtcNow) : null;
                case 1 when length >= 2: chassis = value[0] == 4 ? Convert.ToHexString(value[1..]) : Text(value[1..]); break;
                case 2 when length >= 2: port = value[0] == 3 ? Convert.ToHexString(value[1..]) : Text(value[1..]); break;
                case 3 when length == 2: ttl = BinaryPrimitives.ReadUInt16BigEndian(value); break;
                case 5: name = Text(value); break;
                case 127 when length == 6 && value[..4].SequenceEqual(new byte[] { 0, 0x80, 0xc2, 1 }): vlan = BinaryPrimitives.ReadUInt16BigEndian(value[4..]); break;
            }
        }
        return null;
    }
    private static NeighborAnnouncement? Cdp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] is not (1 or 2) || data[1] == 0) return null;
        var ttl = data[1]; data = data[4..]; string name = "", port = ""; int? vlan = null;
        while (data.Length >= 4)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(data); var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            if (length < 4 || length > data.Length) return null;
            var value = data.Slice(4, length - 4); data = data[length..];
            switch (type)
            {
                case 1: name = Text(value); break;
                case 3: port = Text(value); break;
                case 10 when value.Length == 2: vlan = BinaryPrimitives.ReadUInt16BigEndian(value); break;
            }
        }
        return data.Length == 0 && name.Length > 0 && port.Length > 0 ? new("CDP", name, port, ValidVlan(vlan), ttl, DateTimeOffset.UtcNow) : null;
    }
    private static int? ValidVlan(int? vlan) => vlan is >= 1 and <= 4094 ? vlan : null;
}
