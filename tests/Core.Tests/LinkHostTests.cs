using ProtoSim.Core.Protocols;

namespace ProtoSim.Core.Tests;

/// <summary>Client settings made for a device on this computer, pointed at the same device on another computer.</summary>
public sealed class LinkHostTests
{
    private sealed class Settings
    {
        public string Host { get; set; } = "127.0.0.1";
        public string EndpointUrl { get; set; } = "opc.tcp://localhost:4840/ua";
        public string BaseUrl { get; set; } = "http://localhost:5080/api/v1";
        public string DeviceAddress { get; set; } = "127.0.0.1:47808";
        public string Other { get; set; } = "localhost";
    }

    [Fact]
    public void Retarget_ReplacesLocalHostsOnly()
    {
        var s = (Settings)LinkHost.Retarget(new Settings(), "10.0.0.7");
        Assert.Equal("10.0.0.7", s.Host);
        Assert.Equal("opc.tcp://10.0.0.7:4840/ua", s.EndpointUrl);
        Assert.Equal("http://10.0.0.7:5080/api/v1", s.BaseUrl);
        Assert.Equal("10.0.0.7:47808", s.DeviceAddress);
        Assert.Equal("localhost", s.Other); // not a connection property
    }

    [Fact]
    public void Retarget_KeepsNamedHostsAndLocalTargets()
    {
        var named = (Settings)LinkHost.Retarget(new Settings { Host = "plc-7", EndpointUrl = "opc.tcp://plc-7:4840" }, "10.0.0.7");
        Assert.Equal("plc-7", named.Host);
        Assert.Equal("opc.tcp://plc-7:4840", named.EndpointUrl);

        var local = (Settings)LinkHost.Retarget(new Settings(), "127.0.0.1");
        Assert.Equal("opc.tcp://localhost:4840/ua", local.EndpointUrl);
    }
}
