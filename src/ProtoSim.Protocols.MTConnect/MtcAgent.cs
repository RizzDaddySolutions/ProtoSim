using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using ProtoSim.Core.Data;
using ProtoSim.Core.Protocols;

namespace ProtoSim.Protocols.MTConnect;

/// <summary>An HTTP response produced by the agent.</summary>
internal sealed record MtcResponse(int Status, string Summary, byte[] Body, string ContentType = "text/xml; charset=UTF-8");

/// <summary>
/// MTConnect agent: an HTTP server answering probe, current, sample and assets requests with MTConnect XML.
/// Tags are data items; every value change becomes an observation in the agent's circular buffer.
/// </summary>
public sealed class MtcAgent : TcpFrameServer
{
    private const string DeviceId = "dev";
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

    private readonly MtcSettings _settings;
    private readonly MtcDataModel _model;
    private readonly MtcBuffer _buffer = new();
    private DateTime _modelChangeTime = DateTime.UtcNow;

    public MtcAgent(MtcSettings settings, MtcDataModel model, ServerContext context) : base(settings, context)
    {
        _settings = settings;
        _model = model;
    }

    protected override string ProtocolName => "MTConnect agent";

    protected override Task OnStartedAsync(System.Net.IPAddress bindAddress, int port, CancellationToken serverToken)
    {
        _buffer.Reset(_settings.BufferSize);
        _modelChangeTime = DateTime.UtcNow;
        _buffer.Add(MtcAddress.AvailabilityId, "AVAILABLE", DateTime.UtcNow, true);
        foreach (var (item, tag) in _model.Items()) Record(item, tag, true);
        _model.Store.ValueChanged += OnValueChanged;
        _model.Store.TagsChanged += OnTagsChanged;
        return Task.CompletedTask;
    }

    protected override Task OnStoppingAsync()
    {
        _model.Store.ValueChanged -= OnValueChanged;
        _model.Store.TagsChanged -= OnTagsChanged;
        return Task.CompletedTask;
    }

    private void OnValueChanged(NamedTag tag)
    {
        if (_model.Find(tag.Name) is { } item) Record(item, tag, _settings.RecordDuplicates);
    }

    private void OnTagsChanged()
    {
        _modelChangeTime = DateTime.UtcNow;
        foreach (var (item, tag) in _model.Items()) Record(item, tag, false);
    }

    private void Record(MtcDataItem item, NamedTag tag, bool force) =>
        _buffer.Add(item.Id, MtcAddress.ValueText(item, tag.Value), tag.TimestampUtc, force);

    protected override TcpSession CreateSession(string remoteEndPoint) => new HttpSession(this);

    // ---- Request routing ----

    private static readonly HashSet<string> Requests = new(StringComparer.Ordinal) { "probe", "current", "sample", "assets", "asset" };

    /// <summary>
    /// Answers one GET request target such as /current?path=//Axes, as XML or JSON.
    /// <c>format=json|xml</c> in the query wins, then the Accept header, then the device's default format.
    /// </summary>
    internal MtcResponse Handle(string target, string? accept = null)
    {
        var response = HandleXml(target);
        return WantsJson(target, accept) ? ToJson(response) : response;
    }

    internal bool WantsJson(string target, string? accept)
    {
        int q = target.IndexOf('?');
        if (q >= 0 && ParseQuery(target[(q + 1)..]).TryGetValue("format", out var format))
        {
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase)) return true;
            if (format.Equals("xml", StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (!string.IsNullOrWhiteSpace(accept))
        {
            // The first listed type that we can produce decides.
            foreach (var part in accept.Split(','))
            {
                var type = part.Split(';')[0].Trim().ToLowerInvariant();
                if (type.EndsWith("json", StringComparison.Ordinal)) return true;
                if (type.EndsWith("xml", StringComparison.Ordinal)) return false;
            }
        }
        return _settings.DefaultFormat == MtcFormat.Json;
    }

    internal static MtcResponse ToJson(MtcResponse r) => r with { Body = MtcJson.FromXml(r.Body), ContentType = MtcJson.ContentType };

    private MtcResponse HandleXml(string target)
    {
        string path = target;
        string query = string.Empty;
        int q = target.IndexOf('?');
        if (q >= 0)
        {
            path = target[..q];
            query = target[(q + 1)..];
        }
        var args = ParseQuery(query);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();

        string request;
        string[] rest;
        if (segments.Length == 0)
        {
            request = "probe";
            rest = [];
        }
        else if (Requests.Contains(segments[0]))
        {
            request = segments[0];
            rest = segments[1..];
        }
        else
        {
            if (!string.Equals(segments[0], _settings.DeviceName, StringComparison.Ordinal))
                return Error(404, "NO_DEVICE", $"Could not find the device '{segments[0]}'.");
            request = segments.Length > 1 ? segments[1] : "probe";
            rest = segments.Length > 2 ? segments[2..] : [];
        }

        if (args.ContainsKey("interval"))
            return Error(400, "UNSUPPORTED", "Streaming (interval) is not supported by this simulator; poll current or sample instead.");

        try
        {
            return request switch
            {
                "probe" when rest.Length == 0 => Probe(),
                "current" when rest.Length == 0 => Current(args),
                "sample" when rest.Length == 0 => Sample(args),
                "assets" when rest.Length == 0 => Assets(),
                "assets" or "asset" => Error(404, "ASSET_NOT_FOUND", $"Could not find asset '{string.Join("/", rest)}'."),
                _ => Error(400, "UNSUPPORTED", $"'{path}' is not a supported request; use probe, current, sample or assets."),
            };
        }
        catch (XPathException ex)
        {
            return Error(400, "INVALID_XPATH", $"The path could not be parsed: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string k = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            string v = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    // ---- Documents ----

    private string VersionText
    {
        get
        {
            var parts = (_settings.SchemaVersion ?? "2.0").Trim().Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (parts.Count < 4) parts.Add("0");
            return string.Join('.', parts.Take(4));
        }
    }

    private XNamespace Ns(string document)
    {
        var parts = (_settings.SchemaVersion ?? "2.0").Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        string mm = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : $"{parts.FirstOrDefault() ?? "2"}.0";
        return $"urn:mtconnect.org:{document}:{mm}";
    }

    private static string Time(DateTime utc) => utc.ToUniversalTime().ToString(TimeFormat, CultureInfo.InvariantCulture);

    private XElement Header(XNamespace ns, bool streams)
    {
        var h = new XElement(ns + "Header",
            new XAttribute("creationTime", Time(DateTime.UtcNow)),
            new XAttribute("sender", Environment.MachineName),
            new XAttribute("instanceId", _buffer.InstanceId),
            new XAttribute("version", VersionText),
            new XAttribute("bufferSize", _buffer.Capacity));
        if (streams)
        {
            var (first, last, next) = _buffer.Sequences();
            h.Add(new XAttribute("nextSequence", next), new XAttribute("firstSequence", first), new XAttribute("lastSequence", last));
        }
        else
        {
            h.Add(new XAttribute("assetBufferSize", 1024), new XAttribute("assetCount", 0), new XAttribute("deviceModelChangeTime", Time(_modelChangeTime)));
        }
        return h;
    }

    private sealed class ComponentNode(MtcComponent component, string id)
    {
        public MtcComponent Component { get; } = component;
        public string Id { get; } = id;
        public List<ComponentNode> Children { get; } = [];
        public List<MtcDataItem> Items { get; } = [];
    }

    /// <summary>Builds the component tree; returns the device-level items and top-level components.</summary>
    private (List<MtcDataItem> DeviceItems, List<ComponentNode> Roots, Dictionary<string, ComponentNode?> Owner) Tree()
    {
        var deviceItems = new List<MtcDataItem>();
        var roots = new List<ComponentNode>();
        var owner = new Dictionary<string, ComponentNode?>(StringComparer.Ordinal);
        var byKey = new Dictionary<string, ComponentNode>(StringComparer.Ordinal);

        foreach (var (item, _) in _model.Items())
        {
            ComponentNode? parent = null;
            var key = new StringBuilder();
            foreach (var c in item.Path)
            {
                key.Append('/').Append(c.Type).Append('[').Append(c.Name).Append(']');
                if (!byKey.TryGetValue(key.ToString(), out var node))
                {
                    string id = "c_" + string.Join("_", key.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()));
                    node = new ComponentNode(c, id);
                    byKey[key.ToString()] = node;
                    (parent?.Children ?? roots).Add(node);
                }
                parent = node;
            }
            (parent?.Items ?? deviceItems).Add(item);
            owner[item.Id] = parent;
        }
        return (deviceItems, roots, owner);
    }

    private static readonly MtcDataItem Availability = new(MtcAddress.AvailabilityId, [], "AVAILABILITY", null, MtcCategory.Event, null);

    private XElement DataItemElement(XNamespace ns, MtcDataItem i)
    {
        var e = new XElement(ns + "DataItem",
            new XAttribute("id", i.Id),
            new XAttribute("name", i.Name),
            new XAttribute("type", i.Type),
            new XAttribute("category", i.Category.ToString().ToUpperInvariant()));
        if (i.SubType != null) e.Add(new XAttribute("subType", i.SubType));
        if (i.Units != null) e.Add(new XAttribute("units", i.Units));
        return e;
    }

    private XElement ComponentElement(XNamespace ns, ComponentNode n)
    {
        var e = new XElement(ns + n.Component.Type, new XAttribute("id", n.Id), new XAttribute("name", n.Component.DisplayName));
        if (n.Items.Count > 0) e.Add(new XElement(ns + "DataItems", n.Items.Select(i => DataItemElement(ns, i))));
        if (n.Children.Count > 0) e.Add(new XElement(ns + "Components", n.Children.Select(c => ComponentElement(ns, c))));
        return e;
    }

    /// <summary>The device element of the probe document (in <paramref name="ns"/>, or no namespace for XPath queries).</summary>
    private XElement DeviceElement(XNamespace ns)
    {
        var (deviceItems, roots, _) = Tree();
        var device = new XElement(ns + "Device",
            new XAttribute("id", DeviceId),
            new XAttribute("name", _settings.DeviceName),
            new XAttribute("uuid", _settings.DeviceUuid),
            new XElement(ns + "Description",
                new XAttribute("manufacturer", _settings.Manufacturer ?? string.Empty),
                new XAttribute("model", _settings.Model ?? string.Empty),
                new XAttribute("serialNumber", _settings.SerialNumber ?? string.Empty),
                _settings.Description ?? string.Empty),
            new XElement(ns + "DataItems", new[] { Availability }.Concat(deviceItems).Select(i => DataItemElement(ns, i))));
        if (roots.Count > 0) device.Add(new XElement(ns + "Components", roots.Select(r => ComponentElement(ns, r))));
        return device;
    }

    private MtcResponse Probe()
    {
        var ns = Ns("MTConnectDevices");
        var doc = new XElement(ns + "MTConnectDevices", Header(ns, false), new XElement(ns + "Devices", DeviceElement(ns)));
        return Ok(doc, $"MTConnectDevices · {_model.Items().Count + 1} data items");
    }

    /// <summary>Data item ids selected by an XPath over the probe document, or null for no filter.</summary>
    private HashSet<string>? Filter(Dictionary<string, string> args)
    {
        if (!args.TryGetValue("path", out var xpath) || string.IsNullOrWhiteSpace(xpath)) return null;
        var root = new XDocument(new XElement("MTConnectDevices", new XElement("Devices", DeviceElement(XNamespace.None))));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = root.XPathEvaluate(xpath);
        if (result is not IEnumerable<object> nodes) throw new XPathException("The path must select elements.");
        foreach (var node in nodes.OfType<XElement>())
        {
            if (node.Name.LocalName == "DataItem") ids.Add((string)node.Attribute("id")!);
            else foreach (var d in node.Descendants("DataItem")) ids.Add((string)d.Attribute("id")!);
        }
        if (ids.Count == 0) throw new XPathException("The path matched no data items.");
        return ids;
    }

    private MtcResponse Current(Dictionary<string, string> args)
    {
        var filter = Filter(args);
        Func<string, bool> include = id => (filter == null || filter.Contains(id)) && (id == MtcAddress.AvailabilityId || _model.Find(id) != null);
        IReadOnlyList<Observation> observations;
        if (args.TryGetValue("at", out var atText))
        {
            if (!long.TryParse(atText, NumberStyles.None, CultureInfo.InvariantCulture, out var at))
                return Error(400, "INVALID_REQUEST", $"'at' must be a positive integer, not '{atText}'.");
            var (first, last, _) = _buffer.Sequences();
            if (at < first || at > last)
                return Error(400, "OUT_OF_RANGE", $"'at' must be between {first} and {last}.");
            observations = _buffer.At(at, include);
        }
        else
        {
            observations = _buffer.Latest(include);
        }
        return Streams(observations, null);
    }

    private MtcResponse Sample(Dictionary<string, string> args)
    {
        var filter = Filter(args);
        Func<string, bool> include = id => (filter == null || filter.Contains(id)) && (id == MtcAddress.AvailabilityId || _model.Find(id) != null);
        var (first, _, next) = _buffer.Sequences();

        long from = first;
        if (args.TryGetValue("from", out var fromText) && !long.TryParse(fromText, NumberStyles.None, CultureInfo.InvariantCulture, out from))
            return Error(400, "INVALID_REQUEST", $"'from' must be a positive integer, not '{fromText}'.");
        if (from < first || from > next)
            return Error(400, "OUT_OF_RANGE", $"'from' must be between {first} and {next}.");

        int count = 100;
        if (args.TryGetValue("count", out var countText) && (!int.TryParse(countText, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 1))
            return Error(400, "INVALID_REQUEST", $"'count' must be a positive integer, not '{countText}'.");
        if (count > _settings.MaxSampleCount)
            return Error(400, "OUT_OF_RANGE", $"'count' must be at most {_settings.MaxSampleCount}.");

        var observations = _buffer.Range(from, count, include, out long nextSequence);
        return Streams(observations, nextSequence);
    }

    private MtcResponse Streams(IReadOnlyList<Observation> observations, long? nextSequence)
    {
        var ns = Ns("MTConnectStreams");
        var header = Header(ns, true);
        if (nextSequence is { } n) header.SetAttributeValue("nextSequence", n);

        var (_, _, owner) = Tree();
        var groups = observations.GroupBy(o => o.DataItemId == MtcAddress.AvailabilityId ? null : owner.GetValueOrDefault(o.DataItemId))
            .OrderBy(g => g.Key?.Id ?? "", StringComparer.Ordinal);

        var deviceStream = new XElement(ns + "DeviceStream", new XAttribute("name", _settings.DeviceName), new XAttribute("uuid", _settings.DeviceUuid));
        foreach (var g in groups)
        {
            var cs = new XElement(ns + "ComponentStream",
                new XAttribute("component", g.Key?.Component.Type ?? "Device"),
                new XAttribute("name", g.Key?.Component.DisplayName ?? _settings.DeviceName),
                new XAttribute("componentId", g.Key?.Id ?? DeviceId));
            var samples = new XElement(ns + "Samples");
            var events = new XElement(ns + "Events");
            var conditions = new XElement(ns + "Condition");
            foreach (var o in g.OrderBy(o => o.Sequence))
            {
                var item = o.DataItemId == MtcAddress.AvailabilityId ? Availability : _model.Find(o.DataItemId);
                if (item == null) continue;
                var common = new List<object>
                {
                    new XAttribute("dataItemId", item.Id),
                    new XAttribute("timestamp", Time(o.TimestampUtc)),
                    new XAttribute("name", item.Name),
                    new XAttribute("sequence", o.Sequence),
                };
                if (item.SubType != null) common.Add(new XAttribute("subType", item.SubType));
                switch (item.Category)
                {
                    case MtcCategory.Sample:
                        samples.Add(new XElement(ns + MtcAddress.ElementName(item.Type), common, o.Value));
                        break;
                    case MtcCategory.Event:
                        events.Add(new XElement(ns + MtcAddress.ElementName(item.Type), common, o.Value));
                        break;
                    default:
                        conditions.Add(new XElement(ns + MtcAddress.ElementName(o.Value), common, new XAttribute("type", item.Type)));
                        break;
                }
            }
            foreach (var section in new[] { samples, events, conditions })
                if (section.HasElements) cs.Add(section);
            if (cs.HasElements) deviceStream.Add(cs);
        }

        var doc = new XElement(ns + "MTConnectStreams", header, new XElement(ns + "Streams", deviceStream));
        return Ok(doc, $"MTConnectStreams · {observations.Count} observations");
    }

    private MtcResponse Assets()
    {
        var ns = Ns("MTConnectAssets");
        var header = new XElement(ns + "Header",
            new XAttribute("creationTime", Time(DateTime.UtcNow)),
            new XAttribute("sender", Environment.MachineName),
            new XAttribute("instanceId", _buffer.InstanceId),
            new XAttribute("version", VersionText),
            new XAttribute("assetBufferSize", 1024),
            new XAttribute("assetCount", 0));
        return Ok(new XElement(ns + "MTConnectAssets", header, new XElement(ns + "Assets")), "MTConnectAssets · 0 assets");
    }

    internal MtcResponse Error(int status, string code, string message)
    {
        var ns = Ns("MTConnectError");
        var header = new XElement(ns + "Header",
            new XAttribute("creationTime", Time(DateTime.UtcNow)),
            new XAttribute("sender", Environment.MachineName),
            new XAttribute("instanceId", _buffer.InstanceId),
            new XAttribute("version", VersionText),
            new XAttribute("bufferSize", _buffer.Capacity));
        var doc = new XElement(ns + "MTConnectError", header,
            new XElement(ns + "Errors", new XElement(ns + "Error", new XAttribute("errorCode", code), message)));
        return new MtcResponse(status, $"MTConnectError {code}: {message}", Serialize(doc));
    }

    private static MtcResponse Ok(XElement doc, string summary) => new(200, summary, Serialize(doc));

    private static byte[] Serialize(XElement root)
    {
        using var ms = new MemoryStream();
        using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(w);
        return ms.ToArray();
    }

    // ---- HTTP ----

    private sealed class HttpSession(MtcAgent agent) : TcpSession
    {
        private const int MaxHeaderBytes = 16 * 1024;
        private readonly List<byte> _pending = [];
        private readonly byte[] _buffer = new byte[4096];

        public override async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            while (true)
            {
                int end = IndexOfHeaderEnd();
                if (end >= 0)
                {
                    int headerLength = end + 4;
                    int bodyLength = ContentLength(Encoding.ASCII.GetString(_pending.GetRange(0, headerLength).ToArray()));
                    while (_pending.Count < headerLength + bodyLength)
                    {
                        int n = await stream.ReadAsync(_buffer, cancellationToken);
                        if (n == 0) throw new EndOfStreamException();
                        _pending.AddRange(_buffer.AsSpan(0, n).ToArray());
                    }
                    var frame = _pending.GetRange(0, headerLength).ToArray(); // bodies are ignored (GET only)
                    _pending.RemoveRange(0, headerLength + bodyLength);
                    return frame;
                }
                if (_pending.Count > MaxHeaderBytes)
                    throw new ProtocolFrameException("Request headers too large", Raw(431, "Request Header Fields Too Large", [], "text/plain", false));

                int read = await stream.ReadAsync(_buffer, cancellationToken);
                if (read == 0)
                {
                    if (_pending.Count == 0) return null;
                    throw new EndOfStreamException();
                }
                _pending.AddRange(_buffer.AsSpan(0, read).ToArray());
            }
        }

        private int IndexOfHeaderEnd()
        {
            for (int i = 0; i + 3 < _pending.Count; i++)
                if (_pending[i] == '\r' && _pending[i + 1] == '\n' && _pending[i + 2] == '\r' && _pending[i + 3] == '\n') return i;
            return -1;
        }

        private static int ContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n"))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                    return Math.Min(n, 1 << 20);
            }
            return 0;
        }

        private static (string Method, string Target, bool KeepAlive, string? Accept)? ParseHead(byte[] request)
        {
            var lines = Encoding.ASCII.GetString(request).Split("\r\n");
            var first = lines[0].Split(' ');
            if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
            bool http11 = first[2] == "HTTP/1.1";
            string? Header(string name) => lines.Skip(1)
                .Select(l => (Colon: l.IndexOf(':'), Line: l))
                .Where(x => x.Colon > 0 && x.Line[..x.Colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Line[(x.Colon + 1)..].Trim())
                .FirstOrDefault();
            string? connection = Header("Connection");
            bool keepAlive = http11
                ? !string.Equals(connection, "close", StringComparison.OrdinalIgnoreCase)
                : string.Equals(connection, "keep-alive", StringComparison.OrdinalIgnoreCase);
            return (first[0], first[1], keepAlive, Header("Accept"));
        }

        public override FrameResult Process(byte[] request)
        {
            var head = ParseHead(request);
            if (head == null) return new FrameResult(Raw(400, "Bad Request", [], "text/plain", false), true, true);
            var (method, target, keepAlive, accept) = head.Value;

            MtcResponse r = method is "GET" or "HEAD"
                ? agent.Handle(target, accept)
                : Format(agent.Error(405, "UNSUPPORTED", $"{method} is not supported; MTConnect agents answer GET requests."), target, accept);
            var bytes = Raw(r.Status, Reason(r.Status), method == "HEAD" ? [] : r.Body, r.ContentType, keepAlive, r.Body.Length);
            return new FrameResult(bytes, r.Status >= 400, !keepAlive);
        }

        public override byte[]? BuildFault(byte[] request, int errorCode)
        {
            var head = ParseHead(request);
            var r = Format(agent.Error(500, "INTERNAL_ERROR", $"Fault injection (error code {errorCode})."), head?.Target ?? "/", head?.Accept);
            return Raw(500, Reason(500), r.Body, r.ContentType, head?.KeepAlive ?? false);
        }

        private MtcResponse Format(MtcResponse r, string target, string? accept) => agent.WantsJson(target, accept) ? ToJson(r) : r;

        public override string Describe(byte[] frame, bool isRequest)
        {
            var text = Encoding.UTF8.GetString(frame);
            int eol = text.IndexOf("\r\n", StringComparison.Ordinal);
            string first = eol < 0 ? text : text[..eol];
            if (isRequest) return first;
            int body = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string root = body < 0 ? "" : RootSummary(text[(body + 4)..]);
            return first.Replace("HTTP/1.1 ", "") + (root.Length > 0 ? " · " + root : "");
        }

        private static string RootSummary(string xml)
        {
            if (xml.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(xml);
                    return json.RootElement.EnumerateObject().FirstOrDefault().Name + " (JSON)";
                }
                catch (System.Text.Json.JsonException)
                {
                    return "JSON";
                }
            }
            try
            {
                var root = XElement.Parse(xml);
                return root.Name.LocalName switch
                {
                    "MTConnectStreams" => $"MTConnectStreams · {root.Descendants().Count(e => e.Attribute("sequence") != null)} observations",
                    "MTConnectDevices" => $"MTConnectDevices · {root.Descendants().Count(e => e.Name.LocalName == "DataItem")} data items",
                    "MTConnectError" => "MTConnectError " + string.Join(", ", root.Descendants().Where(e => e.Name.LocalName == "Error").Select(e => (string?)e.Attribute("errorCode"))),
                    _ => root.Name.LocalName,
                };
            }
            catch (XmlException)
            {
                return "";
            }
        }

        private static string Reason(int status) => status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            431 => "Request Header Fields Too Large",
            _ => "Internal Server Error",
        };

        private static byte[] Raw(int status, string reason, byte[] body, string contentType, bool keepAlive, int? contentLength = null)
        {
            var head = new StringBuilder()
                .Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n")
                .Append("Date: ").Append(DateTime.UtcNow.ToString("R", CultureInfo.InvariantCulture)).Append("\r\n")
                .Append("Server: ProtoSim MTConnect\r\n")
                .Append("Content-Type: ").Append(contentType).Append("\r\n")
                .Append("Content-Length: ").Append(contentLength ?? body.Length).Append("\r\n")
                .Append("Access-Control-Allow-Origin: *\r\n")
                .Append("Cache-Control: no-store\r\n")
                .Append("Connection: ").Append(keepAlive ? "keep-alive" : "close").Append("\r\n\r\n");
            var h = Encoding.ASCII.GetBytes(head.ToString());
            var all = new byte[h.Length + body.Length];
            h.CopyTo(all, 0);
            body.CopyTo(all, h.Length);
            return all;
        }
    }
}
