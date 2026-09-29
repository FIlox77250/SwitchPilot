using System.Buffers.Binary;
using System.Text;
using SwitchPilot.Core.Discovery;

namespace SwitchPilot.Tests;

public class NeighborTests
{
    private static byte[] Tlv(int type, params byte[] value)
    {
        var bytes = new byte[value.Length + 2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)((type << 9) | value.Length)); value.CopyTo(bytes, 2); return bytes;
    }
    private static byte[] Frame(byte[] payload, ushort etherType = 0x88cc)
    {
        var bytes = new byte[14 + payload.Length]; BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), etherType); payload.CopyTo(bytes, 14); return bytes;
    }
    private static byte[] Lldp() => Frame([.. Tlv(1, 4, 0, 1, 2, 3, 4, 5), .. Tlv(2, [5, .. Encoding.ASCII.GetBytes("Fa0/14")]), .. Tlv(3, 0, 120), .. Tlv(5, Encoding.ASCII.GetBytes("Switch-A")), .. Tlv(127, 0, 0x80, 0xc2, 1, 0, 10), .. Tlv(0)]);
    [Fact] public void LldpReadsSwitchPortAndOptionalVlan()
    {
        var parsed = NeighborParser.Parse(Lldp()); Assert.NotNull(parsed);
        Assert.Equal("Switch-A", parsed.SwitchName); Assert.Equal("Fa0/14", parsed.Port); Assert.Equal(10, parsed.Vlan); Assert.Equal(120, parsed.TtlSeconds);
    }
    [Fact] public void TruncatedLldpIsRejected()
    {
        var frame = Lldp(); Assert.Null(NeighborParser.Parse(frame[..^3]));
    }
    [Fact] public void LldpMandatoryFieldsCannotBeOmitted() => Assert.Null(NeighborParser.Parse(Frame([.. Tlv(5, Encoding.ASCII.GetBytes("SW")), .. Tlv(0)])));
    [Fact] public void CdpReadsDevicePortAndNativeVlan()
    {
        static byte[] CdpTlv(ushort type, byte[] value)
        {
            var data = new byte[value.Length + 4]; BinaryPrimitives.WriteUInt16BigEndian(data, type); BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), (ushort)data.Length); value.CopyTo(data, 4); return data;
        }
        byte[] payload = [0xaa, 0xaa, 3, 0, 0, 0x0c, 0x20, 0, 2, 180, 0, 0, .. CdpTlv(1, Encoding.ASCII.GetBytes("Cisco-A")), .. CdpTlv(3, Encoding.ASCII.GetBytes("FastEthernet0/14")), .. CdpTlv(10, [0, 20])];
        var parsed = NeighborParser.Parse(Frame(payload, (ushort)payload.Length));
        Assert.NotNull(parsed); Assert.Equal("CDP", parsed.Protocol); Assert.Equal(20, parsed.Vlan); Assert.Equal("Cisco-A", parsed.SwitchName);
    }
    [Fact] public void MalformedPacketsNeverReadOutsideBounds()
    {
        var random = new Random(42);
        for (var i = 0; i < 5000; i++)
        {
            var bytes = new byte[random.Next(0, 1600)]; random.NextBytes(bytes);
            if (bytes.Length >= 14 && i % 2 == 0) { bytes[12] = 0x88; bytes[13] = 0xcc; }
            _ = NeighborParser.Parse(bytes);
        }
    }
    [Fact] public void VlanTaggedLldpIsDecoded()
    {
        var original = Lldp(); byte[] tagged = [.. original[..12], 0x81, 0, 0, 10, .. original[12..]];
        Assert.Equal("Fa0/14", NeighborParser.Parse(tagged)?.Port);
    }
    [Fact] public void EmptyChassisIsRejectedEvenWithSystemName() => Assert.Null(NeighborParser.Parse(Frame([
        .. Tlv(1), .. Tlv(2, 5, 65), .. Tlv(3, 0, 120), .. Tlv(5, 83, 87), .. Tlv(0)])));
    [Fact] public void WrongMacChassisLengthIsRejected() => Assert.Null(NeighborParser.Parse(Frame([
        .. Tlv(1, 4, 1, 2), .. Tlv(2, 5, 65), .. Tlv(3, 0, 120), .. Tlv(0)])));
    [Fact] public void DuplicatedMandatoryTlvIsRejected() => Assert.Null(NeighborParser.Parse(Frame([
        .. Tlv(1, 7, 83), .. Tlv(2, 5, 65), .. Tlv(3, 0, 120), .. Tlv(3, 0, 60), .. Tlv(0)])));
    [Fact] public void ReservedVlanDoesNotBecomeAnAccessVlan()
    {
        var frame = Frame([.. Tlv(1, 7, 83), .. Tlv(2, 5, 65), .. Tlv(3, 0, 120), .. Tlv(127, 0, 0x80, 0xc2, 1, 0x0f, 0xff), .. Tlv(0)]);
        var result = NeighborParser.Parse(frame); Assert.NotNull(result); Assert.Null(result.Vlan);
    }
    [Fact] public void EveryTruncationOfValidLldpIsSafe()
    {
        var bytes = Lldp();
        for (var count = 0; count < bytes.Length; count++) Assert.Null(NeighborParser.Parse(bytes.AsSpan(0, count)));
    }
}
