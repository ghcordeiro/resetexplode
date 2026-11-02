using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ResetExplode;

/// <summary>
/// Roda o PresentMon (Intel, embutido) filtrado pelo processo do jogo. Ele segue cada frame até a tela e grava
/// presentmon.csv: latência até a tela, latência do PC (Reflex), clique até a imagem, CPU e GPU ocupadas por frame,
/// modo de apresentação, tearing, V-Sync e frames descartados. No fim da sessão o arquivo cru é comprimido
/// (presentmon.csv.gz, sem perder nada) e vira exibicao.csv, 1 linha por segundo alinhada com o sensores.csv.
/// </summary>
public sealed class PresentMonProbe : SessionProbe
{
    public const string SessionName = "ResetExplode-PresentMon";
    private Process? _pm;
    private string _folder = "";
    private readonly StringBuilder _log = new();
    private string? _error;

    public override IReadOnlyList<string> Columns { get; } = Array.Empty<string>();

    public override void Start(int pid, string exe, string folder)
    {
        _folder = folder;
        try
        {
            _pm = EmbeddedTool.Start(EmbeddedTool.PresentMon,
                $"--process_id {pid} --output_file \"{Path.Combine(folder, "presentmon.csv")}\" --no_console_stats " +
                $"--session_name {SessionName} --stop_existing_session --terminate_on_proc_exit --track_frame_type --track_pc_latency");
            _pm.OutputDataReceived += (_, e) => Log(e.Data);
            _pm.ErrorDataReceived += (_, e) => Log(e.Data);
            _pm.BeginOutputReadLine();
            _pm.BeginErrorReadLine();
        }
        catch (Exception ex) { _error = ex.Message; }
    }

    private void Log(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_log) { if (_log.Length < 4000) _log.AppendLine(line.Trim()); }
    }

    public override void Fill(double?[] values, int offset) { }

    public override Dictionary<string, object?> Finish()
    {
        StopPresentMon();
        string raw = Path.Combine(_folder, "presentmon.csv");
        if (!File.Exists(raw))
            return new() { ["exibicao"] = new { erro = _error ?? ("PresentMon não gravou dados. " + Tail()) } };
        try
        {
            DisplayAggregator? agg = null;
            using (var r = new StreamReader(new FileStream(raw, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
            {
                string? header = r.ReadLine();
                if (header != null)
                {
                    agg = new DisplayAggregator(header);
                    for (string? line = r.ReadLine(); line != null; line = r.ReadLine()) agg.Add(line);
                }
            }
            if (agg == null || agg.Presents == 0)
                return new() { ["exibicao"] = new { erro = "o jogo não apresentou frames pelo PresentMon (Vulkan/OpenGL sem DXGI?)" } };
            File.WriteAllLines(Path.Combine(_folder, "exibicao.csv"), agg.SecondsCsv(), new UTF8Encoding(false));
            Compress(raw);
            return new() { ["exibicao"] = agg.Summary() };
        }
        catch (Exception ex) { return new() { ["exibicao"] = new { erro = ex.Message } }; }
    }

    private void StopPresentMon()
    {
        if (_pm == null) return;
        try
        {
            if (!_pm.HasExited)
            {
                // Encerra a sessão ETW pelo próprio PresentMon: a instância que grava termina sozinha e fecha o CSV direito.
                using (var stop = EmbeddedTool.Start(EmbeddedTool.PresentMon, $"--terminate_existing_session --session_name {SessionName}"))
                    stop.WaitForExit(5000);
                if (!_pm.WaitForExit(8000)) _pm.Kill();
            }
        }
        catch { try { _pm.Kill(); } catch { } }
        _pm.Dispose();
        _pm = null;
    }

    private static void Compress(string raw)
    {
        using (var src = File.OpenRead(raw))
        using (var dst = new GZipStream(File.Create(raw + ".gz"), CompressionLevel.SmallestSize))
            src.CopyTo(dst);
        File.Delete(raw);
    }

    private string Tail() { lock (_log) return _log.ToString().Trim(); }

    public override void Dispose() => StopPresentMon();
}

/// <summary>
/// Lê as linhas do presentmon.csv (colunas pelo nome do cabeçalho; "NA" = sem valor) e agrega por segundo e no total.
/// Gargalo por segundo, como o PresentMon faz: GPU ocupada ≈ tempo do frame → GPU; CPU ocupada ≈ tempo do frame → CPU;
/// nenhum dos dois → trava de FPS, V-Sync ou espera.
/// </summary>
public sealed class DisplayAggregator
{
    private readonly int _time, _between, _display, _until, _pcLat, _click, _input, _cpu, _gpu, _mode, _tearing, _sync, _frameType;

    private sealed class Bucket
    {
        public int Presents, Displayed, FrameGen, Tearing;
        public double Between, BetweenN, DisplayN, DisplayMax, Until, UntilN, Pc, PcN, Click, ClickN, Input, InputN, Cpu, CpuN, Gpu, GpuN;
        public readonly Dictionary<string, int> Modes = new(), Syncs = new();
    }

    private readonly SortedDictionary<int, Bucket> _seconds = new();
    private readonly List<float> _untilAll = new();
    private double _firstT = double.NaN, _lastT;
    private readonly Dictionary<string, int> _modesAll = new();
    private int _tearingAll, _frameGenAll;
    private double _pcSum, _clickSum, _inputSum, _cpuSum, _gpuSum, _betweenSum;
    private int _pcN, _clickN, _inputN, _cpuN, _gpuN, _betweenN;

    public long Presents { get; private set; }
    public long Displayed { get; private set; }

    public DisplayAggregator(string header)
    {
        var cols = header.Split(',').Select(c => c.Trim()).ToList();
        int I(string n) => cols.FindIndex(c => c.Equals(n, StringComparison.OrdinalIgnoreCase));
        _time = I("TimeInMs"); _between = I("MsBetweenPresents"); _display = I("MsBetweenDisplayChange"); _until = I("MsUntilDisplayed");
        _pcLat = I("MsPCLatency"); _click = I("MsClickToPhotonLatency"); _input = I("MsAllInputToPhotonLatency");
        _cpu = I("MsCPUBusy"); _gpu = I("MsGPUBusy"); _mode = I("PresentMode"); _tearing = I("AllowsTearing"); _sync = I("SyncInterval");
        _frameType = I("FrameType");
        if (_time < 0) throw new InvalidOperationException("presentmon.csv sem a coluna TimeInMs");
    }

    public void Add(string line)
    {
        var f = line.Split(',');
        double? N(int i) => i >= 0 && i < f.Length && double.TryParse(f[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (N(_time) is not double tMs) return;
        double t = tMs / 1000;
        if (double.IsNaN(_firstT)) _firstT = t;
        _lastT = t;
        int sec = (int)Math.Floor(t);
        if (!_seconds.TryGetValue(sec, out var b)) _seconds[sec] = b = new Bucket();
        Presents++; b.Presents++;

        if (N(_between) is double btw) { b.Between += btw; b.BetweenN++; _betweenSum += btw; _betweenN++; }
        if (N(_until) is double until)
        {
            Displayed++; b.Displayed++;
            b.Until += until; b.UntilN++; _untilAll.Add((float)until);
        }
        if (N(_display) is double disp) { b.DisplayN++; b.DisplayMax = Math.Max(b.DisplayMax, disp); }
        if (N(_pcLat) is double pc) { b.Pc += pc; b.PcN++; _pcSum += pc; _pcN++; }
        if (N(_click) is double click) { b.Click += click; b.ClickN++; _clickSum += click; _clickN++; }
        if (N(_input) is double input) { b.Input += input; b.InputN++; _inputSum += input; _inputN++; }
        if (N(_cpu) is double cpu) { b.Cpu += cpu; b.CpuN++; _cpuSum += cpu; _cpuN++; }
        if (N(_gpu) is double gpu) { b.Gpu += gpu; b.GpuN++; _gpuSum += gpu; _gpuN++; }
        if (_mode >= 0 && _mode < f.Length) { string m = f[_mode]; b.Modes[m] = b.Modes.GetValueOrDefault(m) + 1; _modesAll[m] = _modesAll.GetValueOrDefault(m) + 1; }
        if (N(_tearing) == 1) { b.Tearing++; _tearingAll++; }
        if (_sync >= 0 && _sync < f.Length) b.Syncs[f[_sync]] = b.Syncs.GetValueOrDefault(f[_sync]) + 1;
        if (_frameType >= 0 && _frameType < f.Length && IsGenerated(f[_frameType])) { b.FrameGen++; _frameGenAll++; }
    }

    /// <summary>Frame criado por geração de quadros (não renderizado pelo jogo), quando o driver/jogo informa o tipo.</summary>
    public static bool IsGenerated(string frameType) =>
        frameType.Length > 0 && !(frameType is "NA" or "NotSet" or "Unspecified" or "Application" or "Original" or "Repeated");

    /// <summary>"GPU", "CPU" ou "Trava" pelo tempo ocupado médio comparado ao tempo médio entre frames.</summary>
    public static string BoundBy(double frameMs, double? cpuMs, double? gpuMs)
    {
        if (frameMs <= 0) return "";
        if (gpuMs >= frameMs * 0.9) return "GPU";
        if (cpuMs >= frameMs * 0.9) return "CPU";
        return "Trava";
    }

    public IEnumerable<string> SecondsCsv()
    {
        yield return "t_s,presents,exibidos,descartados,fps_exibido,frametime_exibicao_max_ms,latencia_exibicao_ms,latencia_pc_ms," +
                     "latencia_clique_ms,latencia_entrada_ms,cpu_ocupada_ms,gpu_ocupada_ms,gargalo,modo,tearing,sync_interval,frames_gerados";
        foreach (var (sec, b) in _seconds)
        {
            double? Avg(double sum, double n) => n > 0 ? sum / n : null;
            double? frame = Avg(b.Between, b.BetweenN);
            string bound = frame is double fr ? BoundBy(fr, Avg(b.Cpu, b.CpuN), Avg(b.Gpu, b.GpuN)) : "";
            yield return string.Join(',', new[]
            {
                sec.ToString(CultureInfo.InvariantCulture), b.Presents.ToString(CultureInfo.InvariantCulture), b.Displayed.ToString(CultureInfo.InvariantCulture),
                (b.Presents - b.Displayed).ToString(CultureInfo.InvariantCulture),
                // Cada balde é 1 s do relógio do PresentMon: os frames exibidos nele são o FPS exibido daquele segundo.
                b.Displayed.ToString(CultureInfo.InvariantCulture), F(b.DisplayN > 0 ? b.DisplayMax : null),
                F(Avg(b.Until, b.UntilN)), F(Avg(b.Pc, b.PcN)), F(Avg(b.Click, b.ClickN)), F(Avg(b.Input, b.InputN)),
                F(Avg(b.Cpu, b.CpuN)), F(Avg(b.Gpu, b.GpuN)), bound,
                Csv(b.Modes.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? ""), b.Tearing > 0 ? "1" : "0",
                b.Syncs.OrderByDescending(kv => kv.Value).FirstOrDefault().Key ?? "", b.FrameGen.ToString(CultureInfo.InvariantCulture),
            });
        }
    }

    public Dictionary<string, object?> Summary()
    {
        double dur = Math.Max(0.001, _lastT - _firstT);
        var sorted = _untilAll.ToArray();
        Array.Sort(sorted);
        double? P(double p) => sorted.Length == 0 ? null : Math.Round(sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * p))], 2);
        double? Avg(double sum, int n) => n > 0 ? Math.Round(sum / n, 2) : null;
        // Gargalo por segundo, contado em % dos segundos com dados.
        var bounds = _seconds.Values.Where(b => b.BetweenN > 0)
            .Select(b => BoundBy(b.Between / b.BetweenN, b.CpuN > 0 ? b.Cpu / b.CpuN : null, b.GpuN > 0 ? b.Gpu / b.GpuN : null))
            .Where(x => x.Length > 0).ToList();
        return new()
        {
            ["frames_apresentados"] = Presents,
            ["frames_exibidos"] = Displayed,
            ["descartados_pct"] = Math.Round(100.0 * (Presents - Displayed) / Math.Max(1, Presents), 2),
            ["fps_exibido_medio"] = Math.Round(Displayed / dur, 1),
            ["latencia_exibicao_ms"] = new { media = Avg(_untilAll.Sum(x => (double)x), _untilAll.Count), p95 = P(0.95), p99 = P(0.99) },
            ["latencia_pc_ms_media"] = Avg(_pcSum, _pcN),
            ["latencia_clique_ms_media"] = Avg(_clickSum, _clickN),
            ["latencia_clique_amostras"] = _clickN,
            ["latencia_entrada_ms_media"] = Avg(_inputSum, _inputN),
            ["frametime_medio_ms"] = Avg(_betweenSum, _betweenN),
            ["cpu_ocupada_ms_media"] = Avg(_cpuSum, _cpuN),
            ["gpu_ocupada_ms_media"] = Avg(_gpuSum, _gpuN),
            ["gargalo_pct_do_tempo"] = bounds.GroupBy(x => x).OrderByDescending(g => g.Count())
                .ToDictionary(g => g.Key, g => Math.Round(100.0 * g.Count() / bounds.Count)),
            ["modo_apresentacao_pct"] = _modesAll.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => Math.Round(100.0 * kv.Value / Math.Max(1, Presents), 1)),
            ["tearing_permitido_pct"] = Math.Round(100.0 * _tearingAll / Math.Max(1, Presents), 1),
            ["frames_gerados_pct"] = _frameType >= 0 ? Math.Round(100.0 * _frameGenAll / Math.Max(1, Presents), 1) : null,
        };
    }

    private static string F(double? v) => v is double d && double.IsFinite(d) ? d.ToString("0.###", CultureInfo.InvariantCulture) : "";
    private static string Csv(string s) => s.Contains(',') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
