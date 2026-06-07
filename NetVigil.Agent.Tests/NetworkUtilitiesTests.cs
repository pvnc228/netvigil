using System.Net;
using NetVigil.Agent;
using Xunit;

namespace NetVigil.Agent.Tests;

public class NetworkUtilitiesTests
{
    [Theory]
    [InlineData("02:42:AA:BB:CC:DD", true)]   
    [InlineData("0A:11:22:33:44:55", true)]   
    [InlineData("00:11:22:33:44:55", false)]  
    [InlineData("4C:5E:0C:11:22:33", false)]  
    public void IsRandomMac_detects_locally_administered_bit(string mac, bool expected)
    {
        Assert.Equal(expected, NetworkUtilities.IsRandomMac(mac));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Z")]
    public void IsRandomMac_handles_garbage_input(string mac)
    {
        Assert.False(NetworkUtilities.IsRandomMac(mac));
    }

    [Fact]
    public void GetVendorFromMac_returns_PrivateMac_for_locally_administered()
    {
        Assert.Equal("Private MAC", NetworkUtilities.GetVendorFromMac("02:42:AA:BB:CC:DD"));
    }

    [Fact]
    public void GetVendorFromMac_uses_alias_for_known_OUI()
    {
        Assert.Equal("MikroTik", NetworkUtilities.GetVendorFromMac("4C:5E:0C:11:22:33"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AA:BB")]         
    public void GetVendorFromMac_returns_unknown_for_garbage(string mac)
    {
        Assert.Equal("Unknown vendor", NetworkUtilities.GetVendorFromMac(mac));
    }

    [Theory]
    [InlineData("192.168.1.50", "255.255.255.0",   "192.168.1.0/24")]
    [InlineData("10.0.5.7",     "255.0.0.0",       "10.0.0.0/8")]
    [InlineData("172.16.10.20", "255.240.0.0",     "172.16.0.0/12")]
    [InlineData("192.168.1.10", "255.255.255.128", "192.168.1.0/25")]
    [InlineData("192.168.1.200","255.255.255.128", "192.168.1.128/25")]
    public void ToCidr_formats_ip_and_mask_into_canonical_cidr(string ip, string mask, string expected)
    {
        Assert.Equal(expected,
            NetworkUtilities.ToCidr(IPAddress.Parse(ip), IPAddress.Parse(mask)));
    }
}
