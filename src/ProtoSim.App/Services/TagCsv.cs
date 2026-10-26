using System.Globalization;
using System.Text;
using ProtoSim.Core.Data;
using ProtoSim.Core.Simulation;

namespace ProtoSim.App.Services;

/// <summary>Reads and writes tag lists as CSV so they can be edited in a spreadsheet.</summary>
public static class TagCsv
{
    private static readonly string[] Columns =
        ["Name", "Address", "DataType", "WordOrder", "Simulation", "Min", "Max", "PeriodSeconds", "Step", "IntervalMs", "Description"];

    public static void Write(string path, IEnumerable<SimTag> tags)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", Columns));
        foreach (var t in tags)
        {
            var s = t.Simulation;
            sb.AppendLine(string.Join(",",
                Quote(t.Name), Quote(t.Address), t.DataType, t.WordOrder, s.Kind,
                s.Min.ToString(inv), s.Max.ToString(inv), s.PeriodSeconds.ToString(inv), s.Step.ToString(inv),
                s.IntervalMs.ToString(inv), Quote(t.Description)));
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public static List<SimTag> Read(string path)
    {
        var lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count == 0) return [];

        var header = Split(lines[0]);
        int Col(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int name = Col("Name"), address = Col("Address");
        if (name < 0 || address < 0) throw new FormatException("CSV must have at least Name and Address columns.");

        var inv = CultureInfo.InvariantCulture;
        var result = new List<SimTag>();
        for (int i = 1; i < lines.Count; i++)
        {
            var f = Split(lines[i]);
            string Get(string col) { int c = Col(col); return c >= 0 && c < f.Count ? f[c] : string.Empty; }
            double Num(string col, double def) => double.TryParse(Get(col), NumberStyles.Float, inv, out var v) ? v : def;
            T En<T>(string col, T def) where T : struct, Enum => Enum.TryParse<T>(Get(col), true, out var v) ? v : def;

            result.Add(new SimTag
            {
                Name = Get("Name"),
                Address = Get("Address"),
                DataType = En("DataType", TagDataType.UInt16),
                WordOrder = En("WordOrder", WordOrder.ABCD),
                Description = Get("Description"),
                Simulation = new SimulationSettings
                {
                    Kind = En("Simulation", SimulationKind.None),
                    Min = Num("Min", 0),
                    Max = Num("Max", 100),
                    PeriodSeconds = Num("PeriodSeconds", 10),
                    Step = Num("Step", 1),
                    IntervalMs = (int)Num("IntervalMs", 500),
                },
            });
        }
        return result;
    }

    private static string Quote(string s) => s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static List<string> Split(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString().Trim());
        return fields;
    }
}
