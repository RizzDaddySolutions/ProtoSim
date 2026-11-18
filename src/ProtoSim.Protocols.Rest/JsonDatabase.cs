using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProtoSim.Protocols.Rest;

internal enum DbStatus { Ok, Created, NoContent, NotFound, Conflict, BadRequest, MethodNotAllowed }

internal sealed record DbResult(DbStatus Status, JsonNode? Value = null, string? Message = null, int Total = -1)
{
    public static DbResult NotFound(string message) => new(DbStatus.NotFound, Message: message);
    public static DbResult Bad(string message) => new(DbStatus.BadRequest, Message: message);
}

/// <summary>
/// A json-server style document store: a JSON object whose properties are resources.
/// An array property is a collection of objects addressed by their "id"; an object property
/// is a singular resource. Every change is written to disk straight away.
/// </summary>
public sealed class JsonDatabase
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly object _gate = new();
    private JsonObject _root;
    private long _version;

    /// <param name="filePath">Where the database lives. Null keeps it in memory only (tests).</param>
    public JsonDatabase(string? filePath)
    {
        FilePath = filePath;
        if (filePath != null && File.Exists(filePath))
        {
            try
            {
                _root = Validate(JsonNode.Parse(File.ReadAllText(filePath, Encoding.UTF8)));
                return;
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                // Keep the broken file for the user and start over rather than failing the device.
                var backup = Path.ChangeExtension(filePath, $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                File.Move(filePath, backup, true);
                LoadWarning = $"The database file was not valid JSON ({ex.Message}). It was renamed to {Path.GetFileName(backup)} and a new database was created.";
            }
        }
        _root = CreateSample();
        Save();
    }

    public string? FilePath { get; }

    /// <summary>Set when the file on disk could not be loaded.</summary>
    public string? LoadWarning { get; }

    public long Version => Interlocked.Read(ref _version);

    // ---- Whole database ----

    public string ExportJson()
    {
        lock (_gate) return _root.ToJsonString(Indented);
    }

    public JsonObject Snapshot()
    {
        lock (_gate) return (JsonObject)_root.DeepClone();
    }

    public void ImportJson(string json)
    {
        JsonObject root;
        try { root = Validate(JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })); }
        catch (JsonException ex) { throw new FormatException($"Not valid JSON: {ex.Message}", ex); }
        lock (_gate)
        {
            _root = root;
            Save();
        }
    }

    public void Recreate()
    {
        lock (_gate)
        {
            _root = CreateSample();
            Save();
        }
    }

    public IReadOnlyList<(string Name, bool IsCollection, int Count)> GetResources()
    {
        lock (_gate)
            return _root.Select(p => (p.Key, p.Value is JsonArray, p.Value is JsonArray a ? a.Count : p.Value is JsonObject o ? o.Count : 0)).ToList();
    }

    // ---- Resource operations ----

    internal DbResult Read(string resource, string? id)
    {
        lock (_gate)
        {
            if (!_root.TryGetPropertyValue(resource, out var node) || node == null)
                return DbResult.NotFound($"Resource '{resource}' does not exist.");
            if (id == null) return new(DbStatus.Ok, node.DeepClone());
            if (node is not JsonArray items) return DbResult.NotFound($"'{resource}' is a single object, not a collection.");
            var item = Find(items, id);
            return item == null ? DbResult.NotFound($"No item with id '{id}' in '{resource}'.") : new(DbStatus.Ok, item.DeepClone());
        }
    }

    internal DbResult Create(string resource, string? id, JsonNode? body)
    {
        if (id != null) return new(DbStatus.MethodNotAllowed, Message: "POST to the collection, not to an item. Use PUT or PATCH to change an item.");
        if (body is not JsonObject obj) return DbResult.Bad("The request body must be a JSON object.");
        if (!IsValidName(resource)) return DbResult.Bad($"'{resource}' is not a valid resource name.");
        lock (_gate)
        {
            if (!_root.TryGetPropertyValue(resource, out var node) || node == null)
            {
                node = new JsonArray();
                _root[resource] = node;
            }
            if (node is not JsonArray items)
                return new(DbStatus.MethodNotAllowed, Message: $"'{resource}' is a single object; use PUT or PATCH.");

            var item = (JsonObject)obj.DeepClone();
            if (item.TryGetPropertyValue("id", out var given) && given != null)
            {
                if (IdOf(given) is not { Length: > 0 } givenId) return DbResult.Bad("'id' must be a string or number.");
                if (Find(items, givenId) != null) return new(DbStatus.Conflict, Message: $"An item with id '{givenId}' already exists in '{resource}'.");
            }
            else
            {
                // Put the id first so it reads naturally.
                var withId = new JsonObject { ["id"] = NextId(items) };
                foreach (var (k, v) in obj)
                    if (k != "id") withId[k] = v?.DeepClone();
                item = withId;
            }
            items.Add(item);
            Save();
            return new(DbStatus.Created, item.DeepClone());
        }
    }

    internal DbResult Replace(string resource, string? id, JsonNode? body)
    {
        if (body is not JsonObject obj) return DbResult.Bad("The request body must be a JSON object.");
        if (!IsValidName(resource)) return DbResult.Bad($"'{resource}' is not a valid resource name.");
        lock (_gate)
        {
            _root.TryGetPropertyValue(resource, out var node);
            if (id == null)
            {
                if (node is JsonArray) return new(DbStatus.MethodNotAllowed, Message: $"'{resource}' is a collection; PUT to {resource}/{{id}}.");
                _root[resource] = obj.DeepClone();
                Save();
                return new(node == null ? DbStatus.Created : DbStatus.Ok, obj.DeepClone());
            }
            if (node is not JsonArray items) return DbResult.NotFound($"Collection '{resource}' does not exist.");
            int index = IndexOf(items, id);
            if (index < 0) return DbResult.NotFound($"No item with id '{id}' in '{resource}'.");

            var replacement = new JsonObject { ["id"] = items[index]!["id"]!.DeepClone() };
            foreach (var (k, v) in obj)
                if (k != "id") replacement[k] = v?.DeepClone();
            items[index] = replacement;
            Save();
            return new(DbStatus.Ok, replacement.DeepClone());
        }
    }

    /// <summary>JSON Merge Patch (RFC 7386): objects merge recursively and null removes a property.</summary>
    internal DbResult Patch(string resource, string? id, JsonNode? body)
    {
        if (body is not JsonObject patch) return DbResult.Bad("The request body must be a JSON object.");
        lock (_gate)
        {
            if (!_root.TryGetPropertyValue(resource, out var node) || node == null)
                return DbResult.NotFound($"Resource '{resource}' does not exist.");
            JsonObject target;
            if (id == null)
            {
                if (node is not JsonObject o) return new(DbStatus.MethodNotAllowed, Message: $"'{resource}' is a collection; PATCH {resource}/{{id}}.");
                target = o;
            }
            else
            {
                if (node is not JsonArray items) return DbResult.NotFound($"'{resource}' is a single object, not a collection.");
                if (Find(items, id) is not { } item) return DbResult.NotFound($"No item with id '{id}' in '{resource}'.");
                target = item;
                patch = (JsonObject)patch.DeepClone();
                patch.Remove("id");
            }
            Merge(target, patch);
            Save();
            return new(DbStatus.Ok, target.DeepClone());
        }
    }

    internal DbResult Delete(string resource, string? id)
    {
        lock (_gate)
        {
            if (!_root.TryGetPropertyValue(resource, out var node) || node == null)
                return DbResult.NotFound($"Resource '{resource}' does not exist.");
            if (id == null)
                return new(DbStatus.MethodNotAllowed, Message: $"Deleting a whole resource is not allowed over the API. Delete items with DELETE {resource}/{{id}}, or edit the database in ProtoSim.");
            if (node is not JsonArray items) return DbResult.NotFound($"'{resource}' is a single object, not a collection.");
            int index = IndexOf(items, id);
            if (index < 0) return DbResult.NotFound($"No item with id '{id}' in '{resource}'.");
            items.RemoveAt(index);
            Save();
            return new(DbStatus.NoContent);
        }
    }

    // ---- Helpers ----

    private void Save()
    {
        Interlocked.Increment(ref _version);
        if (FilePath == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, _root.ToJsonString(Indented), new UTF8Encoding(false));
        File.Move(tmp, FilePath, true);
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value == null) target.Remove(key);
            else if (value is JsonObject p && target[key] is JsonObject t) Merge(t, p);
            else target[key] = value.DeepClone();
        }
    }

    private static JsonObject? Find(JsonArray items, string id)
    {
        int i = IndexOf(items, id);
        return i < 0 ? null : (JsonObject)items[i]!;
    }

    private static int IndexOf(JsonArray items, string id)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i] is JsonObject o && o.TryGetPropertyValue("id", out var v) && IdOf(v) == id) return i;
        return -1;
    }

    /// <summary>Ids compare as text so 7 and "7" match.</summary>
    internal static string? IdOf(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.ToJsonString(),
        _ => null,
    };

    /// <summary>Next integer when every id is an integer, otherwise a GUID string.</summary>
    private static JsonNode NextId(JsonArray items)
    {
        long max = 0;
        foreach (var item in items)
        {
            if (item is not JsonObject o || !o.TryGetPropertyValue("id", out var v) || v == null) continue;
            if (v is JsonValue jv && jv.GetValueKind() == JsonValueKind.Number && jv.TryGetValue<long>(out var n)) max = Math.Max(max, n);
            else if (IdOf(v) is { } s && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var ns)) max = Math.Max(max, ns);
            else return JsonValue.Create(Guid.NewGuid().ToString())!;
        }
        return JsonValue.Create(max + 1)!;
    }

    internal static bool IsValidName(string name) =>
        name.Length > 0 && !name.StartsWith('_') && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '~');

    private static JsonObject Validate(JsonNode? node)
    {
        if (node is not JsonObject root)
            throw new FormatException("The database must be a JSON object whose properties are collections (arrays of objects) or single objects, e.g. { \"users\": [ { \"id\": 1 } ] }.");
        foreach (var (name, value) in root)
        {
            if (!IsValidName(name))
                throw new FormatException($"'{name}' is not a valid resource name. Use letters, digits, '-', '.' or '~', and don't start with '_'.");
            switch (value)
            {
                case JsonObject:
                    break;
                case JsonArray items:
                    var ids = new HashSet<string>(StringComparer.Ordinal);
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (items[i] is not JsonObject item)
                            throw new FormatException($"'{name}' item {i} is not a JSON object.");
                        if (item.TryGetPropertyValue("id", out var id) && IdOf(id) is { } s && !ids.Add(s))
                            throw new FormatException($"'{name}' has more than one item with id '{s}'.");
                    }
                    break;
                default:
                    throw new FormatException($"'{name}' must be an array of objects or an object.");
            }
        }
        return root;
    }

    public static JsonObject CreateSample() => (JsonObject)JsonNode.Parse("""
        {
          "users": [
            { "id": 1, "name": "Ada Lovelace", "email": "ada@example.com", "role": "admin", "active": true },
            { "id": 2, "name": "Grace Hopper", "email": "grace@example.com", "role": "operator", "active": true },
            { "id": 3, "name": "Alan Turing", "email": "alan@example.com", "role": "viewer", "active": false }
          ],
          "products": [
            { "id": 1, "sku": "PMP-100", "name": "Centrifugal pump", "price": 1249.5, "stock": 12 },
            { "id": 2, "sku": "VLV-220", "name": "Ball valve DN50", "price": 189.0, "stock": 140 },
            { "id": 3, "sku": "SNS-030", "name": "Pressure transmitter", "price": 412.75, "stock": 37 }
          ],
          "orders": [
            { "id": 1, "userId": 2, "productId": 1, "quantity": 2, "status": "shipped", "createdAt": "2026-01-15T09:30:00Z" },
            { "id": 2, "userId": 1, "productId": 3, "quantity": 5, "status": "pending", "createdAt": "2026-02-02T14:05:00Z" }
          ],
          "settings": { "siteName": "ProtoSim plant", "timezone": "UTC", "maintenanceMode": false }
        }
        """)!;
}

/// <summary>json-server style filtering, sorting and paging of a collection.</summary>
internal static class JsonQuery
{
    private static readonly string[] Operators = ["_ne", "_like", "_gte", "_lte", "_gt", "_lt"];

    public static (JsonArray Items, int Total) Apply(JsonArray source, IReadOnlyList<KeyValuePair<string, string>> query)
    {
        IEnumerable<JsonNode?> items = source;

        // Filters: several values for the same key are OR'ed, different keys AND'ed.
        foreach (var group in query.Where(kv => !kv.Key.StartsWith('_') && kv.Key != "q").GroupBy(kv => kv.Key))
        {
            var (field, op) = SplitOperator(group.Key);
            var values = group.Select(kv => kv.Value).ToList();
            items = items.Where(i => values.Any(v => Matches(Resolve(i, field), op, v)));
        }

        if (Get(query, "q") is { Length: > 0 } text)
            items = items.Where(i => ContainsText(i, text));

        var list = items.ToList();
        if (Get(query, "_sort") is { Length: > 0 } sort)
        {
            bool descAll = string.Equals(Get(query, "_order"), "desc", StringComparison.OrdinalIgnoreCase);
            var keys = sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => k.StartsWith('-') ? (Field: k[1..], Desc: !descAll) : (Field: k, Desc: descAll)).ToList();
            list.Sort((a, b) =>
            {
                foreach (var (f, desc) in keys)
                {
                    int c = Compare(Resolve(a, f), Resolve(b, f));
                    if (c != 0) return desc ? -c : c;
                }
                return 0;
            });
        }

        int total = list.Count;
        int? limit = Int(query, "_limit") ?? Int(query, "_per_page");
        if (Int(query, "_page") is { } page)
        {
            int size = limit is > 0 ? limit.Value : 10;
            list = list.Skip(Math.Max(0, page - 1) * size).Take(size).ToList();
        }
        else if (Int(query, "_start") is { } start)
        {
            int end = Int(query, "_end") ?? (limit is { } l ? start + l : int.MaxValue);
            list = list.Skip(start).Take(Math.Max(0, end - start)).ToList();
        }
        else if (limit is { } l)
        {
            list = list.Take(Math.Max(0, l)).ToList();
        }

        return (new JsonArray(list.Select(n => n?.DeepClone()).ToArray()), total);
    }

    private static (string Field, string Op) SplitOperator(string key)
    {
        foreach (var op in Operators)
            if (key.Length > op.Length && key.EndsWith(op, StringComparison.Ordinal)) return (key[..^op.Length], op);
        return (key, string.Empty);
    }

    private static bool Matches(JsonNode? node, string op, string value)
    {
        var text = Text(node);
        switch (op)
        {
            case "_ne": return !string.Equals(text, value, StringComparison.OrdinalIgnoreCase);
            case "_like": return text != null && text.Contains(value, StringComparison.OrdinalIgnoreCase);
        }
        if (op.Length > 0)
        {
            int c = Compare(node, Parse(value));
            if (node == null) return false;
            return op switch { "_gte" => c >= 0, "_lte" => c <= 0, "_gt" => c > 0, "_lt" => c < 0, _ => false };
        }
        return string.Equals(text ?? "null", value, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? Parse(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : JsonValue.Create(value);

    private static bool ContainsText(JsonNode? node, string text) => node switch
    {
        JsonObject o => o.Any(p => ContainsText(p.Value, text)),
        JsonArray a => a.Any(n => ContainsText(n, text)),
        JsonValue => Text(node)?.Contains(text, StringComparison.OrdinalIgnoreCase) == true,
        _ => false,
    };

    /// <summary>Dot paths reach into nested objects and arrays, e.g. address.city or tags.0.</summary>
    internal static JsonNode? Resolve(JsonNode? node, string path)
    {
        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o.TryGetPropertyValue(part, out var v) ? v : null,
                JsonArray a when int.TryParse(part, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node == null) return null;
        }
        return node;
    }

    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => node.ToJsonString(),
    };

    private static int Compare(JsonNode? a, JsonNode? b)
    {
        if (a == null || b == null) return a == null ? (b == null ? 0 : -1) : 1;
        if (a is JsonValue va && b is JsonValue vb && va.GetValueKind() == JsonValueKind.Number && vb.GetValueKind() == JsonValueKind.Number)
            return va.GetValue<double>().CompareTo(vb.GetValue<double>());
        return string.Compare(Text(a), Text(b), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Get(IReadOnlyList<KeyValuePair<string, string>> q, string key) =>
        q.LastOrDefault(kv => kv.Key == key).Value;

    private static int? Int(IReadOnlyList<KeyValuePair<string, string>> q, string key) =>
        int.TryParse(Get(q, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
}
