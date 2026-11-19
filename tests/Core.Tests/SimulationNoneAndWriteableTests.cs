using System.Text.Json;
using ProtoSim.Core.Data;
using ProtoSim.Core.Project;
using ProtoSim.Core.Protocols;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Core.Tests;

public sealed class SimulationNoneAndWriteableTests
{
    [Fact]
    public void None_HoldsTheValue()
    {
        var store = new NamedTagStore();
        var tag = new SimTag
        {
            Name = "Static", Address = "x", DataType = TagDataType.Float64,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.None, IntervalMs = 10 },
            Binding = store.Bind("x", TagDataType.Float64),
        };
        tag.Binding!.Write(12.5);
        for (int i = 0; i < 5; i++) SimulationEngine.Step(tag, i);
        Assert.Equal(12.5, tag.Binding.Read());
        Assert.False(tag.Simulation.IsActive);
        Assert.Equal(SimulationKind.None, tag.Simulation.Kind); // no longer turned into "disabled"
        Assert.True(tag.Simulation.Enabled);
    }

    [Fact]
    public void None_UsesNoParameters()
    {
        Assert.False(SimulationSettings.UsesRange(SimulationKind.None));
        Assert.False(SimulationSettings.UsesPeriod(SimulationKind.None));
        Assert.False(SimulationSettings.UsesStep(SimulationKind.None));
        Assert.False(SimulationSettings.UsesInterval(SimulationKind.None));
        Assert.True(SimulationSettings.UsesInterval(SimulationKind.Sine));
    }

    [Fact]
    public void None_TagValueIsSavedWithTheProject()
    {
        var store = new NamedTagStore();
        var tag = new SimTag
        {
            Name = "Setpoint", Address = "sp", DataType = TagDataType.Int32,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.None },
            Binding = store.Bind("sp", TagDataType.Int32),
        };
        tag.Binding!.Write(42);
        Assert.Equal("42", TagDocument.From(tag).Value);
    }

    [Fact]
    public void Writable_DefaultsToFalse_AndRoundTrips()
    {
        Assert.False(new SimTag().Writable);
        var oldJson = """{"name":"A","address":"HR:1","dataType":"UInt16"}""";
        var doc = JsonSerializer.Deserialize<TagDocument>(oldJson, ProjectSerializer.Options)!;
        Assert.False(doc.Writable);

        var t = new SimTag { Name = "B", Address = "HR:2", Writable = true };
        var back = JsonSerializer.Deserialize<TagDocument>(JsonSerializer.Serialize(TagDocument.From(t), ProjectSerializer.Options), ProjectSerializer.Options)!;
        Assert.True(back.Writable);
        Assert.True(back.ToTag().Writable);
    }

    [Fact]
    public void NewTagBinding_IsReadOnlyForClients()
    {
        var store = new NamedTagStore();
        _ = new SimTag { Binding = store.Bind("fresh", TagDataType.Int32) };
        Assert.False(store.Find("fresh")!.ClientWritable);
    }

    [Fact]
    public void WritableTag_ResetsSimulationToNone()
    {
        var tag = new SimTag
        {
            Writable = false,
            Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Sine },
        };

        tag.Writable = true;

        Assert.Equal(SimulationKind.None, tag.Simulation.Kind);
        Assert.True(tag.Simulation.Enabled);
    }

    [Fact]
    public void SimTag_PushesWritableToItsBinding()
    {
        var store = new NamedTagStore();
        var tag = new SimTag { Writable = false, Binding = store.Bind("v", TagDataType.Int32) };
        Assert.False(store.Find("v")!.ClientWritable);
        tag.Writable = true;
        Assert.True(store.Find("v")!.ClientWritable);
    }

    [Fact]
    public void SharedName_IsReadOnlyWhileAnyTagForbidsWrites()
    {
        var store = new NamedTagStore();
        var a = store.Bind("shared", TagDataType.Int32);
        var b = store.Bind("shared", TagDataType.Int32);
        a.RemoteWritable = false;
        Assert.False(store.Find("shared")!.ClientWritable);
        b.RemoteWritable = false;
        a.RemoteWritable = true;
        Assert.False(store.Find("shared")!.ClientWritable);
        b.Dispose();
        Assert.True(store.Find("shared")!.ClientWritable);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("42", 42L)]
    [InlineData("-1.5", -1.5)]
    [InlineData(" hello ", "hello")]
    public void ClientValue_ParsesText(string text, object expected) => Assert.Equal(expected, ClientValue.Parse(text));
}
