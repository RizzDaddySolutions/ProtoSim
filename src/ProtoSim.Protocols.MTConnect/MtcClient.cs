using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using ProtoSim.Core.Diagnostics;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.MTConnect;

public sealed class MtcClientSettings
{
    [Display(Name = "Agent URL", GroupName = "Connection", Order = 1, Description = "Base URL of the MTConnect agent, e.g. http://192.168.1.30:5000.")]
    public string AgentUrl { get; set; } = "http://localhost:5000";

    [Display(Name = "Device", GroupName = "Connection", Order = 2, Description = "Device name to query, or empty for every device of the agent.")]
    public string Device { get; set; } = string.Empty;

    [Display(Name = "Timeout (ms)", GroupName = "Connection", Order = 3)]
    [Range(100, 600_000)]
    public int TimeoutMs { get; set; } = 5000;
}

public enum MtcClientOperation
{
    /// <summary>The latest value of every data item (one row per data item).</summary>
    Current,
    /// <summary>Recent observations from the agent's buffer.</summary>
    Sample,
    /// <summary>The device model: components and data items.</summary>
    Probe,
    /// <summary>The agent's asset documents.</summary>
    Assets,
}

public sealed class MtcClientRequest
{
    [Display(Name = "Request", GroupName = "Request", Order = 1)]
    public MtcClientOperation Operation { get; set; } = MtcClientOperation.Current;

    [Display(Name = "Path (XPath)", GroupName = "Request", Order = 2, Description = "Optional filter, e.g. //DataItem[@category=\"SAMPLE\"] or //Axes.")]
    public string Path { get; set; } = string.Empty;

    [Display(Name = "From sequence", GroupName = "Request", Order = 3, Description = "Sample: first sequence number; 0 = continue after the last sample request.")]
    [Range(0, long.MaxValue)]
    public long From { get; set; }

    [Display(Name = "Count", GroupName = "Request", Order = 4, Description = "Sample: number of observations.")]
    [Range(1, 100_000)]
    public int Count { get; set; } = 100;
}

/// <summary>MTConnect client: probe, current, sample and assets requests against any agent.</summary>
public sealed class MtcClient : IProtocolClient
{
    private readonly MtcClientSettings _settings;
    private readonly ClientContext _context;
    private readonly HttpClient _http;
    private long _nextSequence;
    private bool _connected;

    public MtcClient(MtcClientSettings settings, ClientContext context)
    {
        _settings = settings;
        _context = context;
        _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs) };
    }

    private ITrafficLog Log => _context.Log;
    private string Remote => _settings.AgentUrl;
    public bool IsConnected => _connected;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var r = await ExecuteAsync(new MtcClientRequest { Operation = MtcClientOperation.Probe }, cancellationToken);
        if (!r.Success) throw new InvalidOperationException(r.Message);
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        _nextSequence = 0;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private string Url(MtcClientRequest r)
    {
        var b = new StringBuilder(_settings.AgentUrl.TrimEnd('/'));
        if (r.Operation != MtcClientOperation.Assets && !string.IsNullOrWhiteSpace(_settings.Device)) b.Append('/').Append(Uri.EscapeDataString(_settings.Device.Trim()));
        b.Append(r.Operation switch
        {
            MtcClientOperation.Probe => "/probe",
            MtcClientOperation.Sample => "/sample",
            MtcClientOperation.Assets => "/assets",
            _ => "/current",
        });
        var q = new List<string> { "format=xml" };
        if (!string.IsNullOrWhiteSpace(r.Path) && r.Operation != MtcClientOperation.Assets) q.Add("path=" + Uri.EscapeDataString(r.Path.Trim()));
        if (r.Operation == MtcClientOperation.Sample)
        {
            long from = r.From > 0 ? r.From : _nextSequence;
            if (from > 0) q.Add("from=" + from);
            q.Add("count=" + Math.Max(1, r.Count));
        }
        return b + "?" + string.Join("&", q);
    }

    public async Task<ClientResult> ExecuteAsync(object request, CancellationToken cancellationToken = default)
    {
        var r = (MtcClientRequest)request;
        var sw = Stopwatch.StartNew();
        try
        {
            var url = Url(r);
            Log.Info(Remote, $"GET {new Uri(url).PathAndQuery}");
            using var resp = await _http.GetAsync(url, cancellationToken);
            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            _connected = true;
            Log.Frame(TrafficDirection.Rx, Remote, bytes, () => $"{(int)resp.StatusCode} {resp.ReasonPhrase} · {bytes.Length} bytes");

            XDocument doc;
            try { doc = XDocument.Parse(Encoding.UTF8.GetString(bytes)); }
            catch (System.Xml.XmlException ex) { return ClientResult.Fail($"{(int)resp.StatusCode}: response is not MTConnect XML ({ex.Message})", sw.Elapsed); }

            var root = doc.Root!;
            if (root.Name.LocalName == "MTConnectError")
            {
                var errors = root.Descendants().Where(e => e.Name.LocalName is "Error" or "ErrorCode" or "Message")
                    .Select(e => $"{(string?)e.Attribute("errorCode") ?? e.Name.LocalName}: {e.Value}").ToList();
                return ClientResult.Fail(errors.Count == 0 ? "Agent returned an error" : string.Join("; ", errors), sw.Elapsed);
            }

            var header = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Header");
            if (r.Operation == MtcClientOperation.Sample && long.TryParse((string?)header?.Attribute("nextSequence"), out var next)) _nextSequence = next;

            var rows = r.Operation switch
            {
                MtcClientOperation.Probe => ProbeRows(root),
                MtcClientOperation.Assets => AssetRows(root),
                MtcClientOperation.Sample => StreamRows(root, sample: true),
                _ => StreamRows(root, sample: false),
            };
            string instance = (string?)header?.Attribute("instanceId") ?? "?";
            return ClientResult.Ok($"{r.Operation}: {rows.Count} rows · instance {instance}", rows, sw.Elapsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            _connected = false;
            Log.Error(Remote, $"No response within {_settings.TimeoutMs} ms");
            return ClientResult.Fail($"No response within {_settings.TimeoutMs} ms.", sw.Elapsed);
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException) _connected = false;
            Log.Error(Remote, $"{r.Operation} failed: {ex.Message}");
            return ClientResult.Fail(ex.Message, sw.Elapsed);
        }
    }

    internal static List<ClientResultRow> StreamRows(XElement root, bool sample)
    {
        var rows = new List<ClientResultRow>();
        foreach (var component in root.Descendants().Where(e => e.Name.LocalName == "ComponentStream"))
        {
            string compName = (string?)component.Attribute("name") ?? (string?)component.Attribute("component") ?? "";
            foreach (var group in component.Elements().Where(e => e.Name.LocalName is "Samples" or "Events" or "Condition"))
            {
                foreach (var obs in group.Elements())
                {
                    string id = (string?)obs.Attribute("dataItemId") ?? "";
                    string name = (string?)obs.Attribute("name") ?? id;
                    string seq = (string?)obs.Attribute("sequence") ?? "";
                    string time = (string?)obs.Attribute("timestamp") ?? "";
                    bool condition = group.Name.LocalName == "Condition";
                    string value = condition ? obs.Name.LocalName + (obs.Value.Length > 0 ? ": " + obs.Value : "") : obs.Value.Trim();
                    string raw = $"{obs.Name.LocalName} · {compName}/{name} · seq {seq} · {time}";
                    object typed = condition || value == "UNAVAILABLE" ? value : ClientValue.Parse(value);
                    rows.Add(new ClientResultRow(sample ? $"{id} @{seq}" : id, raw, value) { Typed = typed });
                }
            }
        }
        return rows;
    }

    private static List<ClientResultRow> ProbeRows(XElement root) =>
        root.Descendants().Where(e => e.Name.LocalName == "DataItem").Select(d =>
        {
            string path = string.Join("/", d.Ancestors().Where(a => a.Name.LocalName is "Device" or "Component" || a.Parent?.Name.LocalName == "Components")
                .Select(a => (string?)a.Attribute("name") ?? a.Name.LocalName).Reverse());
            string raw = $"{(string?)d.Attribute("category")} {(string?)d.Attribute("type")}" +
                         ((string?)d.Attribute("subType") is { } st ? $"/{st}" : "") + ((string?)d.Attribute("units") is { } u ? $" [{u}]" : "");
            string name = (string?)d.Attribute("name") ?? "";
            return new ClientResultRow((string?)d.Attribute("id") ?? "", raw, $"{path}/{name}".TrimStart('/')) { Typed = name };
        }).ToList();

    private static List<ClientResultRow> AssetRows(XElement root) =>
        root.Descendants().Where(e => e.Parent?.Name.LocalName == "Assets").Select(a =>
            new ClientResultRow((string?)a.Attribute("assetId") ?? a.Name.LocalName, $"{a.Name.LocalName} · {(string?)a.Attribute("timestamp")}",
                a.ToString(SaveOptions.DisableFormatting) is { Length: > 300 } s ? s[..300] + "…" : a.ToString(SaveOptions.DisableFormatting))).ToList();
}
