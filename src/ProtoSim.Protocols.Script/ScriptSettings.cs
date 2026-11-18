using System.ComponentModel.DataAnnotations;
using System.IO.Ports;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.Script;

public enum ScriptTransport
{
    Tcp,
    Udp,
    Serial,
}

public enum ScriptFraming
{
    /// <summary>A request ends at CR, LF or CRLF; empty lines are ignored.</summary>
    AnyNewline,
    Cr,
    Lf,
    CrLf,
    /// <summary>A request ends at <see cref="ScriptSettings.CustomTerminator"/>.</summary>
    Custom,
    /// <summary>A request ends when the line goes quiet for <see cref="ScriptSettings.FrameGapMs"/>.</summary>
    Silence,
}

public sealed class ScriptSettings : ITcpServerSettings
{
    public const string DefaultRules = """
        # One rule per line:  request => reply
        #   Plain text matches the whole request (case-insensitive unless turned on below).
        #   /regex/ matches with a regular expression; {$1} or {$name} insert its groups.
        #   {Tag} inserts a tag value, {Tag:F2} formats it, {Flag:ON/OFF} picks a word for Bool tags.
        #   {Tag=$1} or {Tag=42} writes a tag. {now:HH:mm:ss} inserts the time. \r \n \t \xHH are escapes.
        #   Leave the reply empty to stay silent.
        *IDN?                    => ProtoSim,Virtual Instrument,SN0001,1.0
        MEAS:TEMP?               => {Temperature:F2}
        MEAS:PRES?               => {Pressure:F1}
        SOUR:VOLT?               => {Setpoint:F3}
        /SOUR:VOLT (?<v>[-0-9.]+)/ => {Setpoint=$v}OK
        OUTP?                    => {Output:1/0}
        /OUTP (ON|OFF|1|0)/      => {Output=$1}OK
        SYST:TIME?               => {now:HH:mm:ss}
        """;

    [Display(Name = "Transport", GroupName = "Transport", Order = 1, Description = "TCP server, UDP endpoint or serial port.")]
    public ScriptTransport Transport { get; set; } = ScriptTransport.Tcp;

    [Display(Name = "Bind address", GroupName = "Network", Order = 2, Description = "For TCP and UDP. 0.0.0.0 = all IPv4 interfaces, 127.0.0.1 = local only.")]
    public string BindAddress { get; set; } = "0.0.0.0";

    [Display(Name = "Port", GroupName = "Network", Order = 3)]
    [Range(0, 65535)]
    public int Port { get; set; } = 10001;

    [Display(Name = "Max connections", GroupName = "Network", Order = 4, Description = "TCP only.")]
    [Range(1, 1000)]
    public int MaxConnections { get; set; } = 16;

    [Display(Name = "Idle timeout (s)", GroupName = "Network", Order = 5, Description = "TCP only. 0 = never.")]
    [Range(0, 86_400)]
    public int IdleTimeoutSeconds { get; set; }

    [Display(Name = "Serial port", GroupName = "Serial", Order = 10, Description = "COM port, e.g. COM3. A virtual COM port pair (e.g. com0com) works for testing.")]
    public string PortName { get; set; } = "COM1";

    [Display(Name = "Baud rate", GroupName = "Serial", Order = 11)]
    [Range(300, 921_600)]
    public int BaudRate { get; set; } = 9600;

    [Display(Name = "Data bits", GroupName = "Serial", Order = 12)]
    [Range(5, 8)]
    public int DataBits { get; set; } = 8;

    [Display(Name = "Parity", GroupName = "Serial", Order = 13)]
    public Parity Parity { get; set; } = Parity.None;

    [Display(Name = "Stop bits", GroupName = "Serial", Order = 14)]
    public StopBits StopBits { get; set; } = StopBits.One;

    [Display(Name = "Request framing", GroupName = "Framing", Order = 20, Description = "What ends a request. Silence suits binary-ish protocols without a terminator.")]
    public ScriptFraming Framing { get; set; } = ScriptFraming.AnyNewline;

    [Display(Name = "Custom terminator", GroupName = "Framing", Order = 21, Description = @"For Custom framing, e.g. \x03 or ;  (escapes: \r \n \t \xHH).")]
    public string CustomTerminator { get; set; } = string.Empty;

    [Display(Name = "Frame gap (ms)", GroupName = "Framing", Order = 22, Description = "For Silence framing: a pause this long ends the request.")]
    [Range(1, 10_000)]
    public int FrameGapMs { get; set; } = 50;

    [Display(Name = "Reply terminator", GroupName = "Framing", Order = 23, Description = @"Appended to every reply, greeting and periodic message, e.g. \r\n. Empty for none.")]
    public string ReplyTerminator { get; set; } = @"\r\n";

    [Display(Name = "Echo requests", GroupName = "Framing", Order = 24, Description = "Send each request back before the reply, like a terminal with echo on.")]
    public bool Echo { get; set; }

    [Display(Name = "Rules", GroupName = "Script", Order = 30, Description = "request => reply rules, checked from the top; the first match answers.")]
    [DataType(DataType.MultilineText)]
    public string Rules { get; set; } = DefaultRules;

    [Display(Name = "Case-sensitive commands", GroupName = "Script", Order = 31)]
    public bool CaseSensitive { get; set; }

    [Display(Name = "Unknown command reply", GroupName = "Script", Order = 32, Description = "Reply when no rule matches (a template, may use {now} and tags). Empty = stay silent.")]
    public string UnknownReply { get; set; } = "ERR Unknown command";

    [Display(Name = "Error reply", GroupName = "Script", Order = 33,
        Description = "Reply when a rule's write fails (bad value) and for injected errors. {code} is the fault error code.")]
    public string ErrorReply { get; set; } = "ERR {code}";

    [Display(Name = "Greeting", GroupName = "Unsolicited output", Order = 40, Description = "Sent when a TCP client connects (a template). Empty = none.")]
    public string Greeting { get; set; } = string.Empty;

    [Display(Name = "Periodic message", GroupName = "Unsolicited output", Order = 41,
        Description = "Template sent every period without a request, e.g. a scale streaming weight: ST,GS,{Weight:+0000.00}kg. Goes to every TCP client, recent UDP senders, or the serial port.")]
    public string PeriodicMessage { get; set; } = string.Empty;

    [Display(Name = "Period (ms)", GroupName = "Unsolicited output", Order = 42, Description = "0 = off.")]
    [Range(0, 3_600_000)]
    public int PeriodMs { get; set; }
}
