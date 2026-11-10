using System.IO;
using System.Text.RegularExpressions;

namespace ResetExplode;

public sealed record SensorStat(string Label, double Avg, double Max, double Min);

/// <summary>
/// Resumo dos sensores gravados em sensores.csv durante o jogo (só os segundos com o jogo rodando): temperatura, consumo, tensão, clock e carga da
/// CPU e da GPU, VRAM e RAM. As colunas vêm do LibreHardwareMonitor ("Hardware | Nome (unidade)"); cada métrica procura a coluna pelo nome e
/// simplesmente fica de fora se o PC não tem o sensor.
/// </summary>
public static class SensorSummary
{
    private static readonly Regex Gpu = new(@"NVIDIA|GeForce|Radeon|Intel.*Arc|\bGPU\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Cpu = new(@"Ryzen|Core i\d|Intel|AMD|Xeon|\bCPU\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<SensorStat> Compute(string folder)
    {
        var result = new List<SensorStat>();
        var t = CsvTable.Read(Path.Combine(folder, "sensores.csv"), c => c == "fps" || Regex.IsMatch(c, @"\((°C|W|V|MHz|%|MB)\)$"));
        if (t == null || t.Rows.Count < 5) return result;
        var rows = t.Rows.Where(r => (t.Num(r, "fps") ?? 0) > 50).ToList(); // só com o jogo entregando frames
        if (rows.Count < 5) return result;

        static string Hw(string col) { int i = col.IndexOf(" | ", StringComparison.Ordinal); return i < 0 ? "" : col[..i]; }
        static string Nm(string col) { int i = col.IndexOf(" | ", StringComparison.Ordinal); return i < 0 ? col : col[(i + 3)..]; }
        bool IsGpu(string c) => Gpu.IsMatch(Hw(c)) && !Hw(c).Contains("Memory", StringComparison.OrdinalIgnoreCase);
        bool IsCpu(string c) => !IsGpu(c) && Cpu.IsMatch(Hw(c)) && !Hw(c).Contains("Generic", StringComparison.OrdinalIgnoreCase);
        var cols = t.Index.Keys.Where(c => c != "fps").ToList();

        // linhas por segundo: junta as colunas escolhidas por média (clocks) ou por máximo (temperaturas e núcleo mais ocupado)
        void Add(string label, Func<string, bool> pick, bool perRowMax = false)
        {
            var cs = cols.Where(pick).ToList();
            if (cs.Count == 0) return;
            var series = new List<double>();
            foreach (var r in rows)
            {
                var vals = cs.Select(c => t.Num(r, c)).Where(v => v is double d && double.IsFinite(d) && d > 0).Select(v => v!.Value).ToList();
                if (vals.Count > 0) series.Add(perRowMax ? vals.Max() : vals.Average());
            }
            if (series.Count >= 3) result.Add(new SensorStat(label, series.Average(), series.Max(), series.Min()));
        }
        bool Unit(string c, string u) => c.EndsWith($"({u})", StringComparison.Ordinal);
        bool Has(string c, string word) => Nm(c).Contains(word, StringComparison.OrdinalIgnoreCase);

        Add("CPU temperatura (°C)", c => IsCpu(c) && Unit(c, "°C") && Regex.IsMatch(Nm(c), "Tctl|Tdie|Package|CCD"), perRowMax: true);
        Add("CPU potência (W)", c => IsCpu(c) && Unit(c, "W") && Has(c, "Package"));
        Add("CPU tensão Vcore (V)", c => IsCpu(c) && Unit(c, "V") && Regex.IsMatch(Nm(c), @"Core \(SVI2 TFN\)|Vcore", RegexOptions.IgnoreCase));
        Add("CPU tensão pedida VID (V)", c => IsCpu(c) && Unit(c, "V") && Has(c, "VID"));
        Add("CPU clock efetivo médio (MHz)", c => IsCpu(c) && Unit(c, "MHz") && Has(c, "Effective"));
        Add("CPU clock do núcleo mais rápido (MHz)", c => IsCpu(c) && Unit(c, "MHz") && Regex.IsMatch(Nm(c), @"Core #\d+") && !Has(c, "Effective"), perRowMax: true);
        Add("CPU carga total (%)", c => IsCpu(c) && Unit(c, "%") && Has(c, "CPU Total"));
        Add("CPU núcleo mais ocupado (%)", c => IsCpu(c) && Unit(c, "%") && Regex.IsMatch(Nm(c), @"CPU Core #\d+"), perRowMax: true);
        Add("GPU carga (%)", c => IsGpu(c) && Unit(c, "%") && Has(c, "GPU Core"));
        Add("GPU temperatura (°C)", c => IsGpu(c) && Unit(c, "°C") && Has(c, "GPU Core"));
        Add("GPU hotspot (°C)", c => IsGpu(c) && Unit(c, "°C") && Has(c, "Hot Spot"));
        Add("GPU potência (W)", c => IsGpu(c) && Unit(c, "W") && Regex.IsMatch(Nm(c), "Package|Power|Board", RegexOptions.IgnoreCase));
        Add("GPU clock (MHz)", c => IsGpu(c) && Unit(c, "MHz") && Has(c, "GPU Core"));
        Add("GPU tensão (V)", c => IsGpu(c) && Unit(c, "V"));
        Add("VRAM usada (MB)", c => IsGpu(c) && Unit(c, "MB") && Has(c, "Memory Used"));
        Add("RAM em uso (%)", c => Unit(c, "%") && Hw(c).Contains("Memory", StringComparison.OrdinalIgnoreCase) && !Has(c, "Virtual") && Has(c, "Memory"));
        return result;
    }

    /// <summary>Uma linha curta para o laudo.</summary>
    public static string OneLine(IReadOnlyList<SensorStat> s)
    {
        string F(string label, string unit, bool max = false) => s.FirstOrDefault(x => x.Label.StartsWith(label)) is { } x ? $"{(max ? x.Max : x.Avg):0.#}{unit}" : "—";
        var parts = new List<string>();
        if (s.Any(x => x.Label.StartsWith("CPU temperatura"))) parts.Add($"CPU {F("CPU temperatura", " °C", true)} máx, {F("CPU potência", " W")} médios, Vcore {F("CPU tensão Vcore", " V")}, clock efetivo {F("CPU clock efetivo", " MHz")}");
        if (s.Any(x => x.Label.StartsWith("GPU carga"))) parts.Add($"GPU {F("GPU carga", "%")} de uso, {F("GPU temperatura", " °C", true)} máx, {F("GPU potência", " W")}, clock {F("GPU clock", " MHz")}");
        return string.Join(" · ", parts);
    }
}
