using System.Xml.Linq;
using ProtoSim.Protocols.MTConnect;

namespace ProtoSim.MTConnect.Tests;

public sealed class MtcClientTests
{
    [Fact]
    public void Current_ParsesObservations()
    {
        var xml = XDocument.Parse("""
            <MTConnectStreams xmlns="urn:mtconnect.org:MTConnectStreams:2.0">
              <Header instanceId="1" nextSequence="10"/>
              <Streams><DeviceStream name="Mill">
                <ComponentStream component="Linear" name="X">
                  <Samples><Position dataItemId="xpos" name="Xact" sequence="5" timestamp="2026-01-01T00:00:00Z">12.5</Position></Samples>
                  <Events><Execution dataItemId="exec" sequence="6" timestamp="2026-01-01T00:00:00Z">ACTIVE</Execution></Events>
                  <Condition><Normal dataItemId="cond" type="SYSTEM" sequence="7" timestamp="2026-01-01T00:00:00Z"/></Condition>
                </ComponentStream>
              </DeviceStream></Streams>
            </MTConnectStreams>
            """);
        var rows = MtcClient.StreamRows(xml.Root!, sample: false);
        Assert.Equal(3, rows.Count);
        Assert.Equal("xpos", rows[0].Address);
        Assert.Equal(12.5, rows[0].Typed);
        Assert.Equal("ACTIVE", rows[1].Value);
        Assert.Equal("Normal", rows[2].Value);
    }
}
