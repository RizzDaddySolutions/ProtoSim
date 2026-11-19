using ProtoSim.Core.Diagnostics;

namespace ProtoSim.Core.Protocols;

/// <summary>Services a client receives from the host.</summary>
public sealed record ClientContext(ITrafficLog Log);

/// <summary>
/// A protocol client (master, manager, subscriber...) that talks to an external server or device, real or simulated.
/// Requests are plugin-defined objects edited in the UI with the property grid and saved as JSON.
/// </summary>
public interface IProtocolClient : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();

    /// <summary>Runs one request. Protocol errors come back as a failed result; only cancellation throws.</summary>
    Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default);
}

/// <summary>
/// One line of a client result. <see cref="Address"/> identifies the value (it is what a tag mapping refers to),
/// <see cref="Raw"/> shows the wire representation, and <see cref="Value"/> the decoded value.
/// </summary>
public sealed record ClientResultRow(string Address, string Raw, string Value)
{
    /// <summary>The decoded value as a CLR value (bool, number or string), copied into mapped tags. Null = parse <see cref="Value"/>.</summary>
    public object? Typed { get; init; }
}

public sealed record ClientResult(bool Success, string Message, IReadOnlyList<ClientResultRow> Rows, TimeSpan Elapsed)
{
    public static ClientResult Fail(string message, TimeSpan elapsed) => new(false, message, [], elapsed);

    public static ClientResult Ok(string message, IReadOnlyList<ClientResultRow> rows, TimeSpan elapsed) => new(true, message, rows, elapsed);
}

/// <summary>Helpers for clients that receive values as text (MQTT payloads, JSON, XML, SNMP strings...).</summary>
public static class ClientValue
{
    /// <summary>"true"/"false" → bool, integers → long, other numbers → double, anything else stays a string.</summary>
    public static object Parse(string? text)
    {
        var t = (text ?? string.Empty).Trim();
        if (bool.TryParse(t, out var b)) return b;
        if (long.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)) return l;
        if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return t;
    }

    /// <summary>Comma, semicolon or newline separated list, trimmed, without empty entries.</summary>
    public static string[] SplitList(string? text) =>
        (text ?? string.Empty).Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Copies one result row into a project tag after every successful request.</summary>
public sealed class ClientTagMapping
{
    /// <summary>The row's <see cref="ClientResultRow.Address"/>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Project tag as "Device/Tag" (or "Device/Address").</summary>
    public string Target { get; set; } = string.Empty;
}
