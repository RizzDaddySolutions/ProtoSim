using ProtoSim.Core.Data;
using ProtoSim.Core.Logic;
using ProtoSim.Core.Simulation;

namespace ProtoSim.Core.Tests;

public sealed class LogicTests
{
    private sealed class Directory(Dictionary<string, SimTag> tags) : ITagDirectory
    {
        public SimTag? Find(string reference) => tags.GetValueOrDefault(reference);
    }

    private sealed class Builder
    {
        public LogicProgram Program { get; } = new();

        public string Add(string type, params (string Key, string Value)[] ps)
        {
            var b = new LogicBlock { Type = type };
            foreach (var (k, v) in ps) b.Params[k] = v;
            Program.Blocks.Add(b);
            return b.Id;
        }

        public void Wire(string from, string fromPort, string to, string toPort) =>
            Program.Wires.Add(new LogicWire { FromBlock = from, FromPort = fromPort, ToBlock = to, ToPort = toPort });
    }

    private static (Directory Dir, Dictionary<string, SimTag> Tags, NamedTagStore Store) Tags()
    {
        var tags = new Dictionary<string, SimTag>();
        return (new Directory(tags), tags, new NamedTagStore());
    }

    private static SimTag AddTag(Dictionary<string, SimTag> tags, NamedTagStore store, string reference, TagDataType type, object? value = null)
    {
        var t = TagFactory.Tag(store, reference, type, value);
        tags[reference] = t;
        return t;
    }

    private static double Out(CompiledProgram c, string block, int port = 0) => c.Snapshot.Blocks[block].Outputs[port];

    [Fact]
    public void LibraryIsConsistent()
    {
        foreach (var def in BlockLibrary.All)
        {
            foreach (var port in def.Inputs.Where(p => p.FallbackParam != null))
                Assert.Contains(def.Parameters, p => p.Name == port.FallbackParam);
            Assert.Equal(def.Inputs.Count, def.Inputs.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(def, BlockLibrary.Find(def.Type));
        }
    }

    [Fact]
    public void ReadsLogicAndWritesAcrossDevices()
    {
        // Level on a Modbus device drives a pump command on an OPC UA device.
        var (dir, tags, store) = Tags();
        var level = AddTag(tags, store, "Modbus 1/Tank level", TagDataType.Float32, 85f);
        var pump = AddTag(tags, store, "OPC UA 1/Pump run", TagDataType.Bool, true);
        pump.Simulation = new SimulationSettings { Enabled = true, Kind = SimulationKind.Square };

        var b = new Builder();
        var inBlock = b.Add("TAG_IN", ("Tag", "Modbus 1/Tank level"));
        var hi = b.Add("GT", ("B", "80"));
        var not = b.Add("NOT");
        var outBlock = b.Add("TAG_OUT", ("Tag", "OPC UA 1/Pump run"));
        b.Wire(inBlock, "Value", hi, "A");
        b.Wire(hi, "Q", not, "In");
        b.Wire(not, "Q", outBlock, "In");

        using var c = CompiledProgram.Compile(b.Program);
        Assert.Empty(c.Problems);
        c.Scan(dir, 0.1);
        Assert.Equal(false, pump.Binding!.Read());
        Assert.True(pump.IsReplaying); // the program owns the tag; its simulation pauses

        level.Binding!.Write(50f);
        c.Scan(dir, 0.1);
        Assert.Equal(true, pump.Binding.Read());

        c.Dispose();
        Assert.False(pump.IsReplaying);
    }

    [Fact]
    public void UnwiredInputsUseDefaultsAndFallbackParams()
    {
        var (dir, _, _) = Tags();
        var b = new Builder();
        var one = b.Add("CONST", ("Value", "1"));
        var and = b.Add("AND");
        var cmp = b.Add("GE", ("B", "1"));
        b.Wire(one, "Out", and, "A");   // B, C, D unwired: count as true
        b.Wire(one, "Out", cmp, "A");   // B from the parameter
        using var c = CompiledProgram.Compile(b.Program);
        c.Scan(dir, 0.1);
        Assert.Equal(1, Out(c, and));
        Assert.Equal(1, Out(c, cmp));
    }

    [Fact]
    public void OnDelayTimerAndCounter()
    {
        var (dir, _, _) = Tags();
        var b = new Builder();
        var input = b.Add("CONST", ("Value", "1"));
        var ton = b.Add("TON", ("PT", "500"));
        var edge = b.Add("R_TRIG");
        var ctu = b.Add("CTU", ("PV", "2"));
        b.Wire(input, "Out", ton, "In");
        b.Wire(ton, "Q", edge, "CLK");
        b.Wire(edge, "Q", ctu, "CU");
        using var c = CompiledProgram.Compile(b.Program);

        for (int i = 0; i < 4; i++) c.Scan(dir, 0.1);
        Assert.Equal(0, Out(c, ton));
        Assert.Equal(400, Out(c, ton, 1));
        c.Scan(dir, 0.1);
        Assert.Equal(1, Out(c, ton));
        Assert.Equal(1, Out(c, ctu, 1)); // counted the single rising edge
        c.Scan(dir, 0.1);
        Assert.Equal(1, Out(c, ctu, 1)); // still on: no new edge
    }

    [Fact]
    public void PulseAndOffDelayTimers()
    {
        var (dir, tags, store) = Tags();
        var start = AddTag(tags, store, "D/Start", TagDataType.Bool, false);
        var b = new Builder();
        var tin = b.Add("TAG_IN", ("Tag", "D/Start"));
        var tp = b.Add("TP", ("PT", "300"));
        var tof = b.Add("TOF", ("PT", "200"));
        b.Wire(tin, "Value", tp, "In");
        b.Wire(tin, "Value", tof, "In");
        using var c = CompiledProgram.Compile(b.Program);

        start.Binding!.Write(true);
        c.Scan(dir, 0.1);
        Assert.Equal((1.0, 1.0), (Out(c, tp), Out(c, tof)));
        start.Binding.Write(false);
        c.Scan(dir, 0.1);
        Assert.Equal((1.0, 1.0), (Out(c, tp), Out(c, tof)));   // pulse continues; off-delay holds
        c.Scan(dir, 0.1);
        Assert.Equal(0, Out(c, tof));                            // 200 ms after falling
        c.Scan(dir, 0.1);
        Assert.Equal(0, Out(c, tp));                             // 300 ms pulse over
    }

    [Fact]
    public void FeedbackLoopModelsATankWithHysteresisPump()
    {
        // level' = inflow - pump * 4; pump runs (HYST On 80 / Off 20) to drain the tank.
        var (dir, _, _) = Tags();
        var b = new Builder();
        var inflow = b.Add("CONST", ("Value", "2"));
        var drain = b.Add("MUL", ("B", "4"));
        var net = b.Add("SUB");
        var tank = b.Add("INTEG", ("Min", "0"), ("Max", "100"), ("Initial", "50"));
        var hyst = b.Add("HYST", ("On", "80"), ("Off", "20"));
        b.Wire(inflow, "Out", net, "A");
        b.Wire(drain, "Out", net, "B");
        b.Wire(net, "Out", tank, "Rate");
        b.Wire(tank, "Out", hyst, "In");
        b.Wire(hyst, "Q", drain, "A");   // loop: pump state feeds back with one scan delay
        using var c = CompiledProgram.Compile(b.Program);
        Assert.Empty(c.Problems);

        double max = 0, min = 100;
        for (int i = 0; i < 2000; i++)
        {
            c.Scan(dir, 0.1);
            double level = Out(c, tank);
            if (i > 300) { max = Math.Max(max, level); min = Math.Min(min, level); }
        }
        Assert.InRange(max, 79, 82);
        Assert.InRange(min, 18, 21);
    }

    [Fact]
    public void PidDrivesAFirstOrderProcessToSetpoint()
    {
        var (dir, _, _) = Tags();
        var b = new Builder();
        var pid = b.Add("PID", ("SP", "60"), ("Kp", "2"), ("Ti", "4"), ("Td", "0"), ("OutMin", "0"), ("OutMax", "100"));
        var plant = b.Add("LAG", ("Tau", "5"), ("Initial", "20"));
        b.Wire(pid, "Out", plant, "In");
        b.Wire(plant, "Out", pid, "PV");
        using var c = CompiledProgram.Compile(b.Program);
        for (int i = 0; i < 1500; i++) c.Scan(dir, 0.1);
        Assert.InRange(Out(c, plant), 59.5, 60.5);
    }

    [Fact]
    public void ReportsProblemsAndBlockErrors()
    {
        var (dir, _, _) = Tags();
        var b = new Builder();
        var tin = b.Add("TAG_IN", ("Tag", "Nope/Missing"));
        var bad = b.Add("SCALE", ("InMax", "lots"));
        b.Add("WARP_DRIVE");
        b.Wire(tin, "Value", bad, "Bogus");
        using var c = CompiledProgram.Compile(b.Program);
        Assert.Contains(c.Problems, p => p.Contains("WARP_DRIVE"));
        Assert.Contains(c.Problems, p => p.Contains("Bogus"));
        c.Scan(dir, 0.1);
        Assert.Contains("Nope/Missing", c.Snapshot.Blocks[tin].Error);
        Assert.Contains("InMax", c.Snapshot.Blocks[bad].Error);
    }

    [Fact]
    public void RecompilingKeepsState()
    {
        var (dir, _, _) = Tags();
        var b = new Builder();
        var clk = b.Add("CONST", ("Value", "1"));
        var ctu = b.Add("CTU", ("PV", "100"));
        var pulse = b.Add("CLOCK", ("Period", "200"), ("Duty", "50"));
        b.Wire(pulse, "Q", ctu, "CU");
        var c1 = CompiledProgram.Compile(b.Program);
        for (int i = 0; i < 20; i++) c1.Scan(dir, 0.05);
        double counted = Out(c1, ctu, 1);
        Assert.True(counted >= 4);

        b.Program.Blocks.Single(x => x.Id == ctu).Params["PV"] = "5"; // edit while running
        using var c2 = CompiledProgram.Compile(b.Program, c1);
        c2.Scan(dir, 0.05);
        Assert.True(Out(c2, ctu, 1) >= counted);
        Assert.Equal(1, Out(c2, ctu, 0)); // CV ≥ new PV
        _ = clk;
    }

    [Fact]
    public void ProgramsRoundTripThroughTheProjectFile()
    {
        var b = new Builder();
        var a = b.Add("TAG_IN", ("Tag", "Modbus 1/Tank level"));
        var c = b.Add("HYST", ("On", "80"), ("Off", "70"));
        b.Wire(a, "Value", c, "In");
        b.Program.Name = "Tank";
        b.Program.ScanMs = 50;
        b.Program.Blocks[0].X = 120;
        b.Program.Blocks[1].Label = "High level";

        var doc = new Project.ProjectDocument { Logic = [b.Program], LogicAutoRun = true };
        var json = System.Text.Json.JsonSerializer.Serialize(doc, Project.ProjectSerializer.Options);
        var back = System.Text.Json.JsonSerializer.Deserialize<Project.ProjectDocument>(json, Project.ProjectSerializer.Options)!;

        var p = Assert.Single(back.Logic);
        Assert.True(back.LogicAutoRun);
        Assert.Equal((b.Program.Id, "Tank", 50), (p.Id, p.Name, p.ScanMs));
        Assert.Equal(120, p.Blocks[0].X);
        Assert.Equal("Modbus 1/Tank level", p.Blocks[0].Params["Tag"]);
        Assert.Equal("High level", p.Blocks[1].Label);
        var w = Assert.Single(p.Wires);
        Assert.Equal((a, "Value", c, "In"), (w.FromBlock, w.FromPort, w.ToBlock, w.ToPort));

        // Files from before the logic designer have no "logic" property.
        var old = System.Text.Json.JsonSerializer.Deserialize<Project.ProjectDocument>("{\"version\":1,\"devices\":[]}", Project.ProjectSerializer.Options)!;
        Assert.Empty(old.Logic);
    }

    [Fact]
    public async Task EngineRunsProgramsOnTheirScanInterval()
    {
        var (dir, tags, store) = Tags();
        var count = AddTag(tags, store, "Dev/Count", TagDataType.Int32, 0);
        var b = new Builder();
        var pulse = b.Add("CLOCK", ("Period", "100"), ("Duty", "50"));
        var ctu = b.Add("CTU", ("PV", "1000"));
        var output = b.Add("TAG_OUT", ("Tag", "Dev/Count"));
        b.Wire(pulse, "Q", ctu, "CU");
        b.Wire(ctu, "CV", output, "In");
        b.Program.ScanMs = 10;

        using var engine = new LogicEngine(dir);
        engine.Load([b.Program.Clone()]);
        engine.Start();
        await Task.Delay(650, TestContext.Current.CancellationToken);
        var snap = engine.Snapshot(b.Program.Id)!;
        Assert.True(snap.Scans > 20);
        Assert.InRange((int)count.Binding!.Read(), 4, 8);
        Assert.True(count.IsReplaying);

        await engine.StopAsync();
        Assert.False(count.IsReplaying);
    }
}
