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
