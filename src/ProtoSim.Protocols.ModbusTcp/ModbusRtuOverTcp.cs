using System.ComponentModel.DataAnnotations;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.ModbusTcp;

/// <summary>Settings for Modbus RTU framing (address + PDU + CRC, no MBAP header) carried over TCP.</summary>
public sealed class ModbusRtuOverTcpSettings : ModbusDeviceSettings, ITcpServerSettings
{
    public ModbusRtuOverTcpSettings()
    {
        UnmatchedUnitBehavior = UnmatchedUnitBehavior.Ignore;
        TreatUnitZeroAsBroadcast = true;
        ProductName = "Modbus RTU over TCP Simulator";
    }

    [Display(Name = "Bind address", GroupName = "Network", Order = 1, Description = "IP address to listen on. 0.0.0.0 = all IPv4 interfaces, :: = all interfaces (dual stack), 127.0.0.1 = local only.")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Display(Name = "TCP port", GroupName = "Network", Order = 2, Description = "Serial device servers often use 502, 4001 or 8899.")]
    [Range(0, 65535)]
    public int Port { get; set; } = 502;

    [Display(Name = "Max connections", GroupName = "Network", Order = 3, Description = "Further connection attempts are refused.")]
    [Range(1, 1000)]
    public int MaxConnections { get; set; } = 16;

    [Display(Name = "Idle timeout (s)", GroupName = "Network", Order = 4, Description = "Close connections that send nothing for this long. 0 = never.")]
    [Range(0, 86_400)]
    public int IdleTimeoutSeconds { get; set; }

    [Display(Name = "Frame gap (ms)", GroupName = "Network", Order = 5,
        Description = "RTU has no length header. Known function codes are framed by their length; for others, a pause this long ends the frame.")]
    [Range(1, 5000)]
    public int FrameGapMs { get; set; } = 50;
}

/// <summary>Modbus RTU-over-TCP server: each TCP connection carries raw RTU frames.</summary>
public sealed class ModbusRtuOverTcpServer : TcpFrameServer
{
    private readonly ModbusRtuOverTcpSettings _settings;
    private readonly ModbusDataModel _model;

    public ModbusRtuOverTcpServer(ModbusRtuOverTcpSettings settings, ModbusDataModel model, ServerContext context) : base(settings, context)
    {
        _settings = settings;
        _model = model;
    }

    protected override string ProtocolName => "Modbus RTU over TCP";

    protected override TcpSession CreateSession(string remoteEndPoint) =>
        new Session(this, new RtuSlave(_settings, _model), remoteEndPoint);

    private sealed class Session(ModbusRtuOverTcpServer server, RtuSlave slave, string remote) : TcpSession
    {
        private readonly RtuFrameAssembler _assembler = new();
        private readonly byte[] _buffer = new byte[ModbusRtu.MaxAduLength];

        public override async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (_assembler.TryTakeComplete() is { } frame) return frame;
                if (_assembler.Overflowed)
                {
                    _assembler.Clear();
                    throw new ProtocolFrameException($"No valid RTU frame within {ModbusRtu.MaxAduLength} bytes");
                }

                int read;
                if (_assembler.Count > 0)
                {
                    // Part of a frame is buffered: a pause ends it.
                    using var gap = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    gap.CancelAfter(server._settings.FrameGapMs);
                    try
                    {
                        read = await stream.ReadAsync(_buffer, gap.Token);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        return _assembler.TakeOnSilence();
                    }
                }
                else
                {
                    read = await stream.ReadAsync(_buffer, cancellationToken);
                }

                if (read == 0) return _assembler.TakeOnSilence(); // peer closed; null when nothing was pending
                _assembler.Append(_buffer.AsSpan(0, read));
            }
        }

        public override FrameResult Process(byte[] request)
        {
            var d = slave.Classify(request);
            switch (d)
            {
                case RtuDisposition.ForThisDevice:
                    var response = slave.Execute(request, d)!;
                    return new FrameResult(response, RtuSlave.IsException(response));
                case RtuDisposition.OtherUnit when slave.OtherUnitReply(request) is { } reply:
                    return new FrameResult(reply, true);
                case RtuDisposition.Broadcast:
                    slave.Execute(request, d);
                    break;
            }
            RtuLog.NotAnswered(server.Log, server.Context.Statistics, remote, request, d);
            return new FrameResult(null);
        }

        public override bool CanInjectFault(byte[] request) => slave.Classify(request) == RtuDisposition.ForThisDevice;

        public override byte[]? BuildFault(byte[] request, int errorCode) =>
            RtuSlave.Exception(request, (ModbusExceptionCode)Math.Clamp(errorCode, 1, 255));

        public override string Describe(byte[] frame, bool isRequest) => ModbusRtu.Describe(frame, isRequest);
    }
}
