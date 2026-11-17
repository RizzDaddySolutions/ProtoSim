using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProtoSim.Core.Data;

namespace ProtoSim.Protocols.Script;

/// <summary>One "request => reply" rule.</summary>
internal sealed record ScriptRule(int Line, string Pattern, Regex? Regex, string? Literal, string Reply);

/// <summary>Result of running a request through the rules.</summary>
/// <param name="Reply">Text to send (without terminator), or null to stay silent.</param>
/// <param name="Rule">Line number of the rule that matched, or null.</param>
/// <param name="Notes">Problems worth logging (unknown tags, bad values...).</param>
internal sealed record ScriptResult(string? Reply, int? Rule, IReadOnlyList<string> Notes, bool IsError);

/// <summary>Matches requests against rules and renders reply templates with tag values.</summary>
internal sealed class ScriptEngine
{
    private readonly IReadOnlyList<ScriptRule> _rules;
    private readonly NamedTagStore _store;
    private readonly bool _caseSensitive;
    private readonly string _unknownReply;
    private readonly string _errorReply;

    public ScriptEngine(IReadOnlyList<ScriptRule> rules, NamedTagStore store, bool caseSensitive, string unknownReply, string errorReply)
    {
        _rules = rules;
        _store = store;
        _caseSensitive = caseSensitive;
        _unknownReply = unknownReply ?? string.Empty;
        _errorReply = errorReply ?? string.Empty;
    }

    public IReadOnlyList<ScriptRule> Rules => _rules;

    public static ScriptEngine Create(ScriptSettings s, NamedTagStore store) =>
        new(Parse(s.Rules, s.CaseSensitive), store, s.CaseSensitive, s.UnknownReply, s.ErrorReply);

    /// <summary>Parses the rules text. Throws <see cref="FormatException"/> naming the bad line.</summary>
    public static IReadOnlyList<ScriptRule> Parse(string? text, bool caseSensitive)
    {
        var rules = new List<ScriptRule>();
        var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int arrow = line.IndexOf("=>", StringComparison.Ordinal);
            // A regex may itself contain "=>": look for the arrow after its closing slash.
            if (line.StartsWith('/'))
            {
                int close = ClosingSlash(line);
                if (close < 0) throw new FormatException($"Line {i + 1}: the /regex/ isn't closed.");
                arrow = line.IndexOf("=>", close + 1, StringComparison.Ordinal);
            }
            if (arrow < 0) throw new FormatException($"Line {i + 1}: expected 'request => reply'.");

            var pattern = line[..arrow].Trim();
            var reply = line[(arrow + 2)..].Trim();
            if (pattern.Length == 0) throw new FormatException($"Line {i + 1}: the request pattern is empty.");

            Regex? regex = null;
            string? literal = null;
            if (pattern.Length >= 2 && pattern[0] == '/' && pattern[^1] == '/')
            {
                try
                {
                    var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                    regex = new Regex("^(?:" + pattern[1..^1] + ")$", options, TimeSpan.FromMilliseconds(250));
                }
                catch (ArgumentException ex)
                {
                    throw new FormatException($"Line {i + 1}: invalid regular expression: {ex.Message}");
                }
            }
            else
            {
                literal = Unescape(pattern);
            }
            ValidateTemplate(reply, i + 1);
            rules.Add(new ScriptRule(i + 1, pattern, regex, literal, reply));
        }
        return rules;
    }

    private static int ClosingSlash(string line)
    {
        for (int i = 1; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == '/') return i;
        }
        return -1;
    }

    private static void ValidateTemplate(string template, int line)
    {
        int depth = 0;
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c) { i++; continue; }
            if (c == '{') { if (depth++ > 0) throw new FormatException($"Line {line}: nested '{{' in the reply."); }
            else if (c == '}' && depth-- == 0) throw new FormatException($"Line {line}: '}}' without '{{' (write '}}}}' for a literal brace).");
        }
        if (depth != 0) throw new FormatException($"Line {line}: a '{{' in the reply isn't closed.");
    }

    /// <summary>Runs one request (terminator already removed).</summary>
    public ScriptResult Execute(string request)
    {
        var text = request.Trim();
        var comparison = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        foreach (var rule in _rules)
        {
            Match? match = null;
            if (rule.Regex != null)
            {
                try { match = rule.Regex.Match(text); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!match.Success) continue;
            }
            else if (!string.Equals(rule.Literal, text, comparison))
            {
                continue;
            }

            var notes = new List<string>();
            bool failed = false;
            var reply = Render(rule.Reply, match, notes, ref failed);
            if (failed) return new ScriptResult(RenderError(null), rule.Line, notes, true);
            return new ScriptResult(reply.Length == 0 ? null : reply, rule.Line, notes, false);
        }

        if (_unknownReply.Length == 0) return new ScriptResult(null, null, ["no rule matched"], true);
        var n = new List<string> { "no rule matched" };
        bool f = false;
        return new ScriptResult(Render(_unknownReply, null, n, ref f), null, n, true);
    }

    /// <summary>The error reply, with {code} set to <paramref name="code"/> (empty when null).</summary>
    public string RenderError(int? code)
    {
        var notes = new List<string>();
        bool failed = false;
        return Render(_errorReply, null, notes, ref failed, code);
    }

    /// <summary>Renders a template without a match (greeting, periodic message).</summary>
    public string Render(string template, List<string> notes)
    {
        bool failed = false;
        return Render(template, null, notes, ref failed);
    }

    private string Render(string template, Match? match, List<string> notes, ref bool failed, int? code = null)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '\\' && i + 1 < template.Length)
            {
                i = AppendEscape(template, i, sb);
                continue;
            }
            if (c != '{')
            {
                sb.Append(c);
                continue;
            }
            int end = template.IndexOf('}', i + 1);
            if (end < 0) { sb.Append(template[i..]); break; }
            var token = template[(i + 1)..end].Trim();
            i = end;
            sb.Append(Token(token, match, notes, ref failed, code));
        }
        return sb.ToString();
    }

    private string Token(string token, Match? match, List<string> notes, ref bool failed, int? code)
    {
        if (token.StartsWith('$')) return Group(token, match, notes);

        int eq = token.IndexOf('=');
        if (eq > 0)
        {
            var name = token[..eq].Trim();
            var source = token[(eq + 1)..].Trim();
            var value = source.StartsWith('$') ? Group(source, match, notes) : source;
            var tag = _store.Find(name);
            if (tag == null)
            {
                notes.Add($"unknown tag '{name}'");
                failed = true;
                return string.Empty;
            }
            try
            {
                tag.Write(ValueConverter.Parse(value, tag.DataType));
            }
            catch (FormatException ex)
            {
                notes.Add($"can't write '{value}' to {name}: {ex.Message}");
                failed = true;
            }
            return string.Empty;
        }

        int colon = token.IndexOf(':');
        var key = (colon < 0 ? token : token[..colon]).Trim();
        var format = colon < 0 ? null : token[(colon + 1)..];

        if (key.Equals("code", StringComparison.OrdinalIgnoreCase) && _store.Find(key) == null)
            return code?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        if (key.Equals("now", StringComparison.OrdinalIgnoreCase) && _store.Find(key) == null)
            return DateTime.Now.ToString(string.IsNullOrEmpty(format) ? "yyyy-MM-dd'T'HH:mm:ss" : format, CultureInfo.InvariantCulture);

        var t = _store.Find(key);
        if (t == null)
        {
            notes.Add($"unknown tag '{key}'");
            return string.Empty;
        }
        return FormatValue(t.Value, format);
    }

    private static string Group(string token, Match? match, List<string> notes)
    {
        var name = token[1..];
        if (match == null)
        {
            notes.Add($"{token} used outside a /regex/ rule");
            return string.Empty;
        }
        var g = int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? match.Groups[n] : match.Groups[name];
        if (!g.Success) notes.Add($"group {token} didn't match");
        return g.Success ? g.Value : string.Empty;
    }

    internal static string FormatValue(object value, string? format)
    {
        if (string.IsNullOrEmpty(format)) return ValueConverter.Format(value);
        if (value is bool b)
        {
            int slash = format.IndexOf('/');
            return slash < 0 ? (b ? "1" : "0") : b ? format[..slash] : format[(slash + 1)..];
        }
        try
        {
            return value is IFormattable f ? f.ToString(format, CultureInfo.InvariantCulture) : ValueConverter.Format(value);
        }
        catch (FormatException)
        {
            return ValueConverter.Format(value);
        }
    }

    /// <summary>Replaces \r \n \t \0 \\ and \xHH escapes.</summary>
    public static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0) return text;
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length) i = AppendEscape(text, i, sb);
            else sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>Appends the escape starting at <paramref name="i"/> (a backslash); returns the index of its last character.</summary>
    private static int AppendEscape(string s, int i, StringBuilder sb)
    {
        char n = s[i + 1];
        switch (n)
        {
            case 'r': sb.Append('\r'); return i + 1;
            case 'n': sb.Append('\n'); return i + 1;
            case 't': sb.Append('\t'); return i + 1;
            case '0': sb.Append('\0'); return i + 1;
            case '\\': sb.Append('\\'); return i + 1;
            case 'x' when i + 3 < s.Length && byte.TryParse(s.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b):
                sb.Append((char)b);
                return i + 3;
            default:
                sb.Append('\\');
                return i;
        }
    }

    /// <summary>Shows control characters readably for the traffic log.</summary>
    public static string Visible(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                < ' ' or (> '~' and < ' ') => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }
        return sb.ToString();
    }
}
