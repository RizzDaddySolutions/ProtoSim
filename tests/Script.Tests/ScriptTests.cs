using System.Net;
using System.Net.Sockets;
using System.Text;
using ProtoSim.Core.Data;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;
using ProtoSim.Protocols.Script;

namespace ProtoSim.Script.Tests;

public sealed class EngineTests
{
    private static (ScriptEngine Engine, ScriptDataModel Model, ITagBinding Temp, ITagBinding Sp, ITagBinding Out) Setup(string? rules = null, bool caseSensitive = false)
    {
        var model = new ScriptDataModel();
        var temp = model.Bind("Temperature", TagDataType.Float32, WordOrder.ABCD);
        var sp = model.Bind("Setpoint", TagDataType.Float64, WordOrder.ABCD);
        var output = model.Bind("Output", TagDataType.Bool, WordOrder.ABCD);
        temp.Write(21.456f);
        var settings = new ScriptSettings { Rules = rules ?? ScriptSettings.DefaultRules, CaseSensitive = caseSensitive };
        return (ScriptEngine.Create(settings, model.Store), model, temp, sp, output);
    }

    [Fact]
    public void DefaultRulesAnswerLikeAnInstrument()
    {
        var (e, _, _, sp, output) = Setup();
        Assert.Equal("ProtoSim,Virtual Instrument,SN0001,1.0", e.Execute("*IDN?").Reply);
        Assert.Equal("21.46", e.Execute("meas:temp?").Reply); // case-insensitive, formatted
        Assert.Equal("OK", e.Execute("SOUR:VOLT 12.5").Reply);
        Assert.Equal(12.5, sp.Read());
        Assert.Equal("12.500", e.Execute("SOUR:VOLT?").Reply);
        Assert.Equal("OK", e.Execute("OUTP ON").Reply);
        Assert.Equal(true, output.Read());
        Assert.Equal("1", e.Execute("OUTP?").Reply);

        var unknown = e.Execute("FOO");
        Assert.Equal("ERR Unknown command", unknown.Reply);
        Assert.True(unknown.IsError);
        Assert.Null(unknown.Rule);
    }

    [Fact]
    public void TemplatesSupportEscapesBracesGroupsAndBoolWords()
    {
        var (e, _, _, _, output) = Setup("""
            /READ (?<ch>\d)/ => CH{$ch}={Temperature:000.0}\r\nEND
            STATE?           => {Output:RUN/STOP} {{raw}}
            SILENT           =>
            """);
        Assert.Equal("CH3=021.5\r\nEND", e.Execute("READ 3").Reply);
        output.Write(false);
        Assert.Equal("STOP {raw}", e.Execute("STATE?").Reply);
        Assert.Null(e.Execute("SILENT").Reply);
    }

    [Fact]
    public void BadWritesUseTheErrorReply()
    {
        var (e, _, _, sp, _) = Setup("/SET (.+)/ => {Setpoint=$1}OK");
        var r = e.Execute("SET banana");
        Assert.True(r.IsError);
        Assert.Equal("ERR ", r.Reply); // {code} is empty outside fault injection
        Assert.Equal(0.0, sp.Read());
        Assert.Contains(r.Notes, n => n.Contains("banana"));
    }

    [Fact]
    public void CaseSensitivityIsOptional()
    {
        var (e, _, _, _, _) = Setup("PING => PONG", caseSensitive: true);
        Assert.Equal("PONG", e.Execute("PING").Reply);
        Assert.Equal("ERR Unknown command", e.Execute("ping").Reply);
    }

    [Theory]
    [InlineData("no arrow here")]
    [InlineData("/unclosed => x")]
    [InlineData("/(/ => x")]
    [InlineData("A => {Temperature")]
    [InlineData("A => }")]
    [InlineData(" => reply")]
    public void RejectsBadRules(string rules)
    {
        var ex = Assert.Throws<FormatException>(() => ScriptEngine.Parse(rules, false));
        Assert.StartsWith("Line 1", ex.Message);
    }

    [Fact]
    public void ReservedAndInvalidTagNamesAreRejected()
    {
        var m = new ScriptDataModel();
        Assert.Throws<FormatException>(() => m.Bind("now", TagDataType.Int32, WordOrder.ABCD));
        Assert.Throws<FormatException>(() => m.Bind("2fast", TagDataType.Int32, WordOrder.ABCD));
        Assert.Throws<FormatException>(() => m.Bind("has space", TagDataType.Int32, WordOrder.ABCD));
    }
}

public sealed class FramerTests
{
    private static List<string> Frames(ScriptFraming framing, string input, string custom = "")
    {
        var f = new ScriptFramer(framing, Encoding.Latin1.GetBytes(ScriptEngine.Unescape(custom)));
        f.Append(Encoding.Latin1.GetBytes(input));
        var list = new List<string>();
        while (f.TakeComplete() is { } frame) list.Add(Encoding.Latin1.GetString(frame));
        return list;
    }

    [Fact]
    public void SplitsByTerminator()
    {
        Assert.Equal(["A", "B", "C"], Frames(ScriptFraming.AnyNewline, "A\r\nB\n\nC\rD"));
        Assert.Equal(["A\nB"], Frames(ScriptFraming.Cr, "A\nB\rC"));
        Assert.Equal(["A"], Frames(ScriptFraming.CrLf, "A\r\nB\r"));
        Assert.Equal(["STX1", "STX2"], Frames(ScriptFraming.Custom, "STX1\x03STX2\x03", @"\x03"));
        Assert.Empty(Frames(ScriptFraming.Silence, "ABC"));
    }
}

public sealed class TransportTests : IAsyncLifetime
{
    private readonly ScriptDataModel _model = new();
    private readonly FaultSettings _faults = new();
    private readonly List<ITagBinding> _bindings = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        var w = _model.Bind("Weight", TagDataType.Float32, WordOrder.ABCD);
        w.Write(12.5f);
        _bindings.Add(w);
        _bindings.Add(_model.Bind("Tare", TagDataType.Float32, WordOrder.ABCD));
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var b in _bindings) b.Dispose();
        return ValueTask.CompletedTask;
    }

    private ScriptSettings Settings(ScriptTransport transport) => new()
    {
        Transport = transport,
        BindAddress = "127.0.0.1",
        Port = 0,
        Rules = """
            W     => ST,GS,{Weight:+0000.00}kg
            /T (.+)/ => {Tare=$1}OK
            """,
    };

    private ServerContext Context() => new(new TrafficLog(), _faults, new ServerStatistics());

    private static async Task<string> ReadLineAsync(NetworkStream s)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(3000);
        while (await s.ReadAsync(one, cts.Token) == 1)
        {
            if (one[0] == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)one[0]);
        }
        return sb.ToString();
    }

    [Fact]
    public async Task TcpAnswersSendsGreetingAndPeriodicOutput()
    {
        var settings = Settings(ScriptTransport.Tcp);
        settings.Greeting = "SCALE READY";
        await using var server = new ScriptTcpServer(settings, _model, Context());
        await server.StartAsync(Ct);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint!.Port, Ct);
        var s = client.GetStream();

        Assert.Equal("SCALE READY", await ReadLineAsync(s));
        await s.WriteAsync("W\r\n"u8.ToArray(), Ct);
        Assert.Equal("ST,GS,+0012.50kg", await ReadLineAsync(s));
        await s.WriteAsync("T 1.25\n"u8.ToArray(), Ct);
        Assert.Equal("OK", await ReadLineAsync(s));
        Assert.Equal(1.25f, _bindings[1].Read());
        await s.WriteAsync("BOGUS\r\n"u8.ToArray(), Ct);
        Assert.Equal("ERR Unknown command", await ReadLineAsync(s));

        _faults.ErrorResponsePercent = 100;
        _faults.ErrorCode = 7;
        await s.WriteAsync("W\r\n"u8.ToArray(), Ct);
        Assert.Equal("ERR 7", await ReadLineAsync(s));
    }

    [Fact]
    public async Task TcpStreamsPeriodicMessages()
    {
        var settings = Settings(ScriptTransport.Tcp);
        settings.PeriodicMessage = "WT {Weight:F1}";
        settings.PeriodMs = 50;
        await using var server = new ScriptTcpServer(settings, _model, Context());
        await server.StartAsync(Ct);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.LocalEndPoint!.Port, Ct);
        var s = client.GetStream();
        Assert.Equal("WT 12.5", await ReadLineAsync(s));
        Assert.Equal("WT 12.5", await ReadLineAsync(s));
    }

    [Fact]
    public async Task UdpRepliesToEachDatagram()
    {
        await using var server = new ScriptUdpServer(Settings(ScriptTransport.Udp), _model, Context());
        await server.StartAsync(Ct);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await udp.SendAsync("W"u8.ToArray(), server.LocalEndPoint!, Ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(3000);
        var reply = await udp.ReceiveAsync(cts.Token);
        Assert.Equal("ST,GS,+0012.50kg\r\n", Encoding.Latin1.GetString(reply.Buffer));
        Assert.Single(server.GetConnections());
    }

    [Fact]
    public async Task SerialStartFailsClearlyForMissingPort()
    {
        var settings = Settings(ScriptTransport.Serial);
        settings.PortName = "COM199";
        await using var server = new ScriptSerialServer(settings, _model, Context());
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(Ct));
        Assert.Equal(ServerState.Faulted, server.State);
        Assert.Contains("COM199", server.LastError);
    }

    [Fact]
    public async Task BadRulesStopTheDeviceFromStarting()
    {
        var settings = Settings(ScriptTransport.Tcp);
        settings.Rules = "OK => fine\nbroken line";
        await using var server = new ScriptTcpServer(settings, _model, Context());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => server.StartAsync(Ct));
        Assert.Contains("Line 2", ex.Message);
    }
}
