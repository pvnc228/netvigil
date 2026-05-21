using NetVigil.Server.Services;
using NetVigil.Shared;
using Xunit;

namespace NetVigil.Server.Tests;

public class AgentRegistryTests
{
    private static AgentInfoSnapshot New(string id, string mode = "ArpScan")
        => new()
        {
            AgentId = id,
            Hostname = $"host-{id}",
            InterfaceName = "eth0",
            LocalIp = "192.168.1.10",
            SubnetCidr = "192.168.1.0/24",
            Mode = mode,
            Version = "1.0.0",
            GrpcTarget = "http://localhost:5002",
            StartedAt = DateTime.UtcNow
        };

    [Fact]
    public void Update_inserts_then_overwrites_by_agent_id()
    {
        var reg = new AgentRegistry();
        reg.Update(New("a1", "ArpScan"));
        reg.Update(New("a1", "GatewaySniffer"));

        var all = reg.GetAll();
        Assert.Single(all);
        Assert.Equal("GatewaySniffer", all[0].Mode);
    }

    [Fact]
    public void Update_ignores_blank_agent_id()
    {
        var reg = new AgentRegistry();
        reg.Update(New(""));
        reg.Update(New("   "));
        Assert.Empty(reg.GetAll());
    }

    [Fact]
    public void GetActive_filters_out_stale_entries()
    {
        var reg = new AgentRegistry();
        reg.Update(New("a1"));

        var stale = reg.GetActive(TimeSpan.FromMilliseconds(-1));
        Assert.Empty(stale);

        var fresh = reg.GetActive(TimeSpan.FromSeconds(60));
        Assert.Single(fresh);
        Assert.Equal("a1", fresh[0].AgentId);
    }

    [Fact]
    public void Multiple_agents_are_returned_sorted_by_hostname()
    {
        var reg = new AgentRegistry();
        reg.Update(New("z"));
        reg.Update(New("a"));
        reg.Update(New("m"));

        var all = reg.GetAll();
        Assert.Equal(new[] { "host-a", "host-m", "host-z" },
                     all.Select(a => a.Hostname).ToArray());
    }
}
