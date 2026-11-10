using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>
/// Lê o console.log do CS2 (opção de inicialização -condebug). O mapa de benchmark imprime ao terminar
/// "[VProf] FPS: Avg=517.3, P1=183.5" (P1 = 1% low). Só o fim da passada é conhecido; o início é marcado pelo usuário.
/// </summary>
public static class VProfLog
{
    private static readonly Regex Summary = new(@"FPS:\s*Avg\s*=\s*(\d+(?:[.,]\d+)?)\s*,\s*P1\s*=\s*(\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static (double Avg, double P1)? TryParseSummary(string? line)
    {
        if (string.IsNullOrEmpty(line) || !line.Contains("VProf", StringComparison.OrdinalIgnoreCase)) return null;
        var m = Summary.Match(line);
        if (!m.Success) return null;
        static double N(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
        return (N(m.Groups[1].Value), N(m.Groups[2].Value));
    }

    public sealed record VProfRow(string Name, double AllAvg, double AllP99, int ActiveN, double ActiveAvg, double ActiveP99, double MaxP50, double MaxP95);
    public sealed record VProfReport(int Frames, int Intervals, int Excluded, double? Avg, double? P1, List<VProfRow> Rows, DateTime? EndTime = null);

    private static readonly Regex Stamp = new(@"^\s*(\d{1,2})/(\d{1,2})\s+(\d{1,2}):(\d{2}):(\d{2})\b", RegexOptions.Compiled);

    /// <summary>Hora que o console.log põe no começo de cada linha ("11/10 06:25:41 [VProf] ..." = mês/dia). <c>null</c> se a linha não tem.</summary>
    public static DateTime? TryParseStamp(string line)
    {
        var m = Stamp.Match(line);
        if (!m.Success) return null;
        try { return new DateTime(DateTime.Now.Year, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value)); }
        catch { return null; }
    }

    private static readonly Regex SummaryFrames = new(@"Summary of\s+(\d+)\s+frames and\s+(\d+)\s+1-second intervals\.?\s*(?:\((\d+)\s+frames excluded)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Row = new(@"^\s*(?:\[VProf\])?\s*([A-Za-z_][A-Za-z_ ]*?)\s+(-?\d+(?:[.,]\d+)?)\s+(-?\d+(?:[.,]\d+)?)\s+(\d+)\s+(-?\d+(?:[.,]\d+)?)\s+(-?\d+(?:[.,]\d+)?)\s+(-?\d+(?:[.,]\d+)?)\s+(-?\d+(?:[.,]\d+)?)\s+(\d+)\s+(-?\d+(?:[.,]\d+)?)\s+(-?\d+(?:[.,]\d+)?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Relatório completo do mapa: "Summary of N frames and M 1-second intervals. (K frames excluded from analysis.)", a linha FPS e a tabela por sistema
    /// (FrameTotal, Client Rendering, Server Simulation...). <c>null</c> se não há a linha de resumo de frames.
    /// </summary>
    public static VProfReport? ParseReport(IEnumerable<string> lines)
    {
        int frames = 0, intervals = 0, excluded = 0; double? avg = null, p1 = null; bool seen = false; DateTime? end = null;
        var rows = new List<VProfRow>();
        static double N(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
        foreach (var line in lines)
        {
            var m = SummaryFrames.Match(line);
            if (m.Success)
            {
                seen = true; frames = int.Parse(m.Groups[1].Value); intervals = int.Parse(m.Groups[2].Value);
                end = TryParseStamp(line) ?? end;
                excluded = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
                continue;
            }
            if (TryParseSummary(line) is { } f) { avg = f.Avg; p1 = f.P1; continue; }
            var r = Row.Match(line);
            if (r.Success && !r.Groups[1].Value.Contains("frames", StringComparison.OrdinalIgnoreCase))
                rows.Add(new VProfRow(r.Groups[1].Value.Trim(), N(r.Groups[2].Value), N(r.Groups[3].Value), int.Parse(r.Groups[4].Value), N(r.Groups[5].Value),
                                      N(r.Groups[6].Value), N(r.Groups[7].Value), N(r.Groups[8].Value)));
        }
        return seen ? new VProfReport(frames, intervals, excluded, avg, p1, rows, end) : null;
    }

    /// <summary>console.log a partir do cs2.exe (…\game\bin\win64\cs2.exe → …\game\csgo\console.log).</summary>
    public static string? PathFromExe(string cs2Exe)
    {
        try
        {
            string? dir = Path.GetDirectoryName(cs2Exe);
            return dir == null ? null : Path.GetFullPath(Path.Combine(dir, "..", "..", "csgo", "console.log"));
        }
        catch { return null; }
    }

    /// <summary>Diferença percentual do 1% low calculado em relação ao P1 impresso pelo mapa.</summary>
    public static double DiffPct(double computed, double printed) => printed == 0 ? 0 : 100 * (computed - printed) / printed;
}

/// <summary>Acompanha um arquivo que só cresce: <see cref="ReadNew"/> devolve as linhas acrescentadas desde a última chamada (ou desde a criação).</summary>
public sealed class LogTail
{
    private readonly string _path;
    private long _pos;
    private string _partial = "";

    public LogTail(string path)
    {
        _path = path;
        try { _pos = File.Exists(path) ? new FileInfo(path).Length : 0; } catch { _pos = 0; }
    }

    public IEnumerable<string> ReadNew()
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(_path)) return lines;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _pos) { _pos = 0; _partial = ""; } // arquivo recomeçou (o jogo reabriu)
            fs.Seek(_pos, SeekOrigin.Begin);
            using var r = new StreamReader(fs, Encoding.UTF8);
            string text = _partial + r.ReadToEnd();
            _pos = fs.Length;
            var parts = text.Split('\n');
            _partial = parts[^1]; // última linha ainda incompleta
            for (int i = 0; i < parts.Length - 1; i++) lines.Add(parts[i].TrimEnd('\r'));
        }
        catch { }
        return lines;
    }
}
