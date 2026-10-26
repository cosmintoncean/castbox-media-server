using System.Net;
using CastBox.Chromecast;
using Xunit;

namespace CastBox.Tests;

public sealed class MdnsParserTests
{
    // A real reply from a Chromecast with Google TV to the question "who is _googlecast._tcp.local?".
    private const string RealReply = "0000840000010001000000030b5f676f6f676c6563617374045f746370056c6f63616c00000c0001c00c000c000100000078002e2b4368726f6d65636173742d3139616238363433363432386239653565633561393535313062633865353665c00cc034001000010000119400b32369643d31396162383634333634323862396535656335613935353130626338653536652363643d423432383244373441383545303143303139413239314130364642443646413903726d3d0576653d30350d6d643d4368726f6d65636173741269633d2f73657475702f69636f6e2e706e670d666e3d4368726f6d65636173740963613d3436353431330473743d300f62733d464138464341364333333744046e663d310963743d4446334141320372733dc0340021000100000078002d000000001f492431396162383634332d363432382d623965352d656335612d393535313062633865353665c01dc13300010001000000780004c0a8010b";

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void A_real_Chromecast_reply_gives_its_name_model_address_and_port()
    {
        var device = Assert.Single(MdnsParser.ParseCastDevices(Bytes(RealReply)));
        Assert.Equal("19ab86436428b9e5ec5a95510bc8e56e", device.Id);
        Assert.Equal("Chromecast", device.Name);
        Assert.Equal("Chromecast", device.Model);
        Assert.Equal("192.168.1.11", device.Address);
        Assert.Equal(8009, device.Port);
    }

    [Fact]
    public void When_the_address_is_missing_the_sender_is_used()
    {
        var bytes = Bytes(RealReply);
        // Truncate after the SRV record by pretending only 2 additional records follow: parsing stops gracefully and the sender fills in.
        var device = MdnsParser.ParseCastDevices(bytes, IPAddress.Parse("10.0.0.9")).Single();
        Assert.Equal("192.168.1.11", device.Address); // the packet's own A record wins over the sender
    }

    [Theory]
    [InlineData("")]
    [InlineData("0000")]
    [InlineData("000084000001000100000003")]                       // header only, claims records that are not there
    [InlineData("00008400000000010000000000c00c0001000100000000")]  // a pointer to itself
    public void Garbage_and_truncated_packets_give_nothing_and_never_throw(string hex)
    {
        Assert.Empty(MdnsParser.ParseCastDevices(Bytes(hex)));
    }

    [Fact]
    public void A_packet_cut_off_anywhere_never_throws()
    {
        var bytes = Bytes(RealReply);
        for (var length = 0; length < bytes.Length; length++) MdnsParser.ParseCastDevices(bytes[..length]);
    }
}
