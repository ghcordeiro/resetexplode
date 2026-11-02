using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ResetExplode;

/// <summary>
/// Uma leitura por segundo de tudo que não é frame. <paramref name="Metrics"/> segue a ordem das colunas de métricas
/// passadas ao criar a sessão (sondas: processo do jogo, PCIe, rede...); <paramref name="Sensors"/>, a das colunas de sensores.
/// </summary>
public sealed record SessionSecond(
    bool Foreground, string Limiter, int? GpuPState, string? GpuPerfCap, bool GpuThermalCap,
    float? CpuTemp, float? GpuTemp, float? CpuPower, float? GpuPower,
    IReadOnlyList<double?> Metrics, IReadOnlyList<float?> Sensors);

/// <summary>Resumo dos frames: FPS médio, lows e percentis de frametime.</summary>
public sealed record FrameSummary(long Frames, double Seconds, double AvgFps, double Low1Fps, double Low01Fps,
                                  double P50Ms, double P95Ms, double P99Ms, double P999Ms, double MaxMs);

/// <summary>
/// Grava uma sessão de jogo em %AppData%\ResetExplode\sessoes\&lt;data&gt;-&lt;jogo&gt;\:
/// frames.csv (todo frame: instante e frametime), sensores.csv (1/s: FPS, processo do jogo, NVML e TODOS os sensores),
/// eventos.csv (engasgos, alt-tab, limitador térmico, CPU quente) e resumo.json no final.
/// Os frames chegam pela thread do ETW (<see cref="OnPresent"/>) e são gravados a cada <see cref="Tick"/> (1 s, thread da tela).
/// </summary>
public sealed class GameSession : IDisposable
{
    public static string Root => Path.Combine(AppSettings.Folder, "sessoes");
    public const double StutterMinMs = 33.4; // engasgo: frame >= 3x a mediana do segundo e >= 33,4 ms (menos de 30 FPS naquele frame)

    public string Folder { get; }
    public string Game { get; }
    public string Exe { get; }
    public int Pid { get; }
    public DateTime Start { get; } = DateTime.Now;
    public long FrameCount => _frameTimes.Count;
    public int Stutters { get; private set; }
    public TimeSpan Elapsed => DateTime.Now - Start;

    private readonly ConcurrentQueue<double> _pending = new();
    private readonly List<float> _frameTimes = new(1 << 16);
    private readonly StreamWriter _frames, _sensors, _events;
    private double? _t0, _lastT;
    private bool _wasForeground = true, _wasThermal, _wasCpuHot;
    private readonly Dictionary<string, int> _limiters = new();
    private int _seconds, _fgSeconds;
    private float _maxCpuTemp, _maxGpuTemp;
    private double _sumCpuPower, _sumGpuPower; private int _nCpuPower, _nGpuPower;

    public GameSession(string game, string exe, int pid, IReadOnlyList<string> sensorColumns, IReadOnlyList<string> metricColumns)
    {
        Game = game; Exe = exe; Pid = pid;
        string safe = string.Concat(game.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        Folder = Path.Combine(Root, $"{Start:yyyy-MM-dd_HH-mm-ss}_{(safe.Length > 0 ? safe : "jogo")}");
        Directory.CreateDirectory(Folder);

        _frames = Open("frames.csv");
        _frames.WriteLine("t_ms,frametime_ms");
        _sensors = Open("sensores.csv");
        _sensors.WriteLine(string.Join(',', new[]
        {
            "t_s", "fps", "frametime_mediana_ms", "frametime_max_ms", "frames", "primeiro_plano", "limitador",
            "gpu_pstate", "gpu_perfcap"
        }.Concat(metricColumns).Concat(sensorColumns).Select(Csv)));
        _events = Open("eventos.csv");
        _events.WriteLine("t_s,tipo,detalhe");
        Event("inicio", $"{game} ({Path.GetFileName(exe)}, PID {pid})");
    }

    private StreamWriter Open(string name) =>
        new(Path.Combine(Folder, name), false, new UTF8Encoding(false), 1 << 16) { AutoFlush = false };

    /// <summary>Thread do ETW: só enfileira.</summary>
    public void OnPresent(int pid, double t)
    {
        if (pid == Pid) _pending.Enqueue(t);
    }

    /// <summary>Chamado a cada segundo: grava os frames que chegaram e a linha de sensores.</summary>
    public void Tick(SessionSecond s)
    {
        double ts = Elapsed.TotalSeconds;
        var fts = new List<float>();
        while (_pending.TryDequeue(out double t))
        {
            _t0 ??= t;
            if (_lastT is double prev && t > prev)
            {
                float ft = (float)(t - prev);
                fts.Add(ft);
                _frameTimes.Add(ft);
                _frames.Write((t - _t0.Value).ToString("0.###", CultureInfo.InvariantCulture));
                _frames.Write(',');
                _frames.WriteLine(ft.ToString("0.###", CultureInfo.InvariantCulture));
            }
            _lastT = t;
        }

        double median = Percentile(fts, 0.5);
        foreach (var ft in fts)
            if (IsStutter(ft, median)) { Stutters++; Event("engasgo", string.Create(CultureInfo.InvariantCulture, $"{ft:0.0} ms (mediana {median:0.0} ms)")); }

        double fps = fts.Count > 0 ? 1000.0 * fts.Count / fts.Sum(x => (double)x) : 0;

        if (s.Foreground != _wasForeground) Event(s.Foreground ? "primeiro_plano" : "segundo_plano", s.Foreground ? "voltou ao jogo" : "saiu do jogo (alt-tab ou outra janela)");
        if (s.GpuThermalCap != _wasThermal) Event(s.GpuThermalCap ? "gpu_termico_inicio" : "gpu_termico_fim", s.GpuPerfCap ?? "");
        bool cpuHot = s.CpuTemp >= 95;
        if (cpuHot != _wasCpuHot) Event(cpuHot ? "cpu_quente_inicio" : "cpu_quente_fim", s.CpuTemp is float c ? string.Create(CultureInfo.InvariantCulture, $"{c:0} °C") : "");
        _wasForeground = s.Foreground; _wasThermal = s.GpuThermalCap; _wasCpuHot = cpuHot;

        _seconds++;
        if (s.Foreground) _fgSeconds++;
        if (fts.Count > 0) _limiters[s.Limiter] = _limiters.GetValueOrDefault(s.Limiter) + 1;
        if (s.CpuTemp is float ct) _maxCpuTemp = Math.Max(_maxCpuTemp, ct);
        if (s.GpuTemp is float gt) _maxGpuTemp = Math.Max(_maxGpuTemp, gt);
        if (s.CpuPower is float cp) { _sumCpuPower += cp; _nCpuPower++; }
        if (s.GpuPower is float gp) { _sumGpuPower += gp; _nGpuPower++; }

        var row = new List<string>
        {
            F(ts, "0.0"), F(fps, "0.0"), F(median, "0.###"), F(fts.Count > 0 ? fts.Max() : 0, "0.###"), fts.Count.ToString(CultureInfo.InvariantCulture),
            s.Foreground ? "1" : "0", s.Limiter, s.GpuPState?.ToString(CultureInfo.InvariantCulture) ?? "", Csv(s.GpuPerfCap ?? ""),
        };
        row.AddRange(s.Metrics.Select(v => F(v, "0.###")));
        row.AddRange(s.Sensors.Select(v => F(v, "0.###")));
        _sensors.WriteLine(string.Join(',', row));

        // Grava em disco a cada 10 s: se o PC travar, perde-se no máximo isso.
        if (_seconds % 10 == 0) { _frames.Flush(); _sensors.Flush(); _events.Flush(); }
    }

    /// <summary>Fecha os arquivos e grava o resumo.json. <paramref name="extra"/> entra no resumo (hardware, erros de hardware...).</summary>
    public FrameSummary Finish(string reason, IReadOnlyDictionary<string, object?> extra)
    {
        Event("fim", reason);
        var sum = Summarize(_frameTimes);
        var resumo = new Dictionary<string, object?>
        {
            ["jogo"] = Game,
            ["executavel"] = Path.GetFileName(Exe),
            ["inicio"] = Start.ToString("s"),
            ["duracao_s"] = Math.Round(Elapsed.TotalSeconds),
            ["motivo_fim"] = reason,
            ["frames"] = sum.Frames,
            ["fps_medio"] = Math.Round(sum.AvgFps, 1),
            ["low_1pct_fps"] = Math.Round(sum.Low1Fps, 1),
            ["low_01pct_fps"] = Math.Round(sum.Low01Fps, 1),
            ["frametime_ms"] = new { p50 = R(sum.P50Ms), p95 = R(sum.P95Ms), p99 = R(sum.P99Ms), p999 = R(sum.P999Ms), max = R(sum.MaxMs) },
            ["engasgos"] = Stutters,
            ["tempo_em_primeiro_plano_pct"] = _seconds > 0 ? Math.Round(100.0 * _fgSeconds / _seconds) : null,
            ["limitador_pct_do_tempo"] = _limiters.OrderByDescending(kv => kv.Value)
                .ToDictionary(kv => kv.Key, kv => Math.Round(100.0 * kv.Value / Math.Max(1, _limiters.Values.Sum()))),
            ["cpu_temp_max_c"] = _maxCpuTemp > 0 ? _maxCpuTemp : null,
            ["gpu_temp_max_c"] = _maxGpuTemp > 0 ? _maxGpuTemp : null,
            ["cpu_consumo_medio_w"] = _nCpuPower > 0 ? Math.Round(_sumCpuPower / _nCpuPower, 1) : null,
            ["gpu_consumo_medio_w"] = _nGpuPower > 0 ? Math.Round(_sumGpuPower / _nGpuPower, 1) : null,
            ["arquivos"] = new { frames = "frames.csv (t_ms, frametime_ms de cada frame)", sensores = "sensores.csv (1 linha por segundo)", eventos = "eventos.csv" },
        };
        foreach (var kv in extra) resumo[kv.Key] = kv.Value;

        Dispose();
        File.WriteAllText(Path.Combine(Folder, "resumo.json"),
            JsonSerializer.Serialize(resumo, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
        return sum;
    }

    public void Dispose()
    {
        foreach (var w in new[] { _frames, _sensors, _events })
            try { w.Dispose(); } catch { }
    }

    /// <summary>Registra um evento avulso (ex.: uma sonda que não conseguiu começar).</summary>
    public void Note(string type, string detail) => Event(type, detail);

    private void Event(string type, string detail)
    {
        _events.WriteLine($"{F(Elapsed.TotalSeconds, "0.0")},{type},{Csv(detail)}");
    }

    // ------------------------------------------------------------------ cálculo (sem dependências, testável)

    public static bool IsStutter(float frameMs, double medianMs) => medianMs > 0 && frameMs >= Math.Max(3 * medianMs, StutterMinMs);

    /// <summary>Lows pelo percentil do frametime (mesmo critério do overlay): 1% low = 1000 / p99.</summary>
    public static FrameSummary Summarize(IReadOnlyList<float> frameTimes)
    {
        if (frameTimes.Count == 0) return new FrameSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var sorted = frameTimes.ToArray();
        Array.Sort(sorted);
        double total = 0;
        foreach (var f in sorted) total += f;
        double p99 = Percentile(sorted, 0.99), p999 = Percentile(sorted, 0.999);
        return new FrameSummary(sorted.Length, total / 1000, 1000.0 * sorted.Length / total,
            p99 > 0 ? 1000 / p99 : 0, p999 > 0 ? 1000 / p999 : 0,
            Percentile(sorted, 0.5), Percentile(sorted, 0.95), p99, p999, sorted[^1]);
    }

    /// <summary>Percentil por posição (a lista precisa estar ordenada, exceto para a mediana de poucos valores).</summary>
    private static double Percentile(IReadOnlyList<float> values, double p)
    {
        if (values.Count == 0) return 0;
        IReadOnlyList<float> v = values;
        if (p == 0.5) { var c = values.ToArray(); Array.Sort(c); v = c; }
        return v[(int)Math.Min(v.Count - 1, Math.Floor(v.Count * p))];
    }

    private static double R(double x) => Math.Round(x, 2);
    private static string F(double? v, string fmt) => v is double d && double.IsFinite(d) ? d.ToString(fmt, CultureInfo.InvariantCulture) : "";
    private static string F(float? v, string fmt) => F(v is float f ? f : (double?)null, fmt);
    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>Sessões gravadas (lidas do resumo.json de cada pasta) e limpeza das mais antigas.</summary>
public static class SessionStore
{
    public sealed record Item(string Folder, string Game, DateTime Start, double Seconds, double AvgFps, double Low1, double Low01, int Stutters, long Bytes,
                              IReadOnlyList<string> Avisos, string? Veredito)
    {
        public string VereditoText => Veredito != null ? $"LAUDO: {Veredito.ToUpperInvariant()}" : "";
        public string AvisosText => Avisos.Count == 0 ? "" : (Avisos.Count == 1 ? Avisos[0] : $"{Avisos.Count} avisos · {Avisos[0]}");
        public string Title => Game;
        public string When => $"{Start:dd/MM/yyyy HH:mm} · {TimeSpan.FromSeconds(Seconds):h\\:mm\\:ss}";
        public string Stats => AvgFps > 0 ? $"{AvgFps:0} FPS · 1% {Low1:0} · 0,1% {Low01:0} · {Stutters} engasgos" : "sem frames medidos";
        public string Size => $"{Bytes / 1048576.0:0.#} MB";
    }

    public const long MaxTotalBytes = 3L << 30; // 3 GB: acima disso apaga as mais antigas

    public static List<Item> List()
    {
        var list = new List<Item>();
        if (!Directory.Exists(GameSession.Root)) return list;
        foreach (var dir in Directory.GetDirectories(GameSession.Root))
        {
            try
            {
                long bytes = new DirectoryInfo(dir).GetFiles().Sum(f => f.Length);
                string resumo = Path.Combine(dir, "resumo.json");
                if (!File.Exists(resumo)) { list.Add(new Item(dir, Path.GetFileName(dir) + " (incompleta)", Directory.GetCreationTime(dir), 0, 0, 0, 0, 0, bytes, Array.Empty<string>(), null)); continue; }
                using var doc = JsonDocument.Parse(File.ReadAllText(resumo));
                var r = doc.RootElement;
                double D(string k) => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
                var avisos = r.TryGetProperty("avisos", out var av) && av.ValueKind == JsonValueKind.Array
                    ? av.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                string? veredito = r.TryGetProperty("laudo", out var lj) && lj.TryGetProperty("veredito", out var vj) ? vj.GetString() : null;
                list.Add(new Item(dir, r.GetProperty("jogo").GetString() ?? "?", DateTime.Parse(r.GetProperty("inicio").GetString()!, CultureInfo.InvariantCulture),
                    D("duracao_s"), D("fps_medio"), D("low_1pct_fps"), D("low_01pct_fps"), (int)D("engasgos"), bytes, avisos, veredito));
            }
            catch { }
        }
        return list.OrderByDescending(i => i.Start).ToList();
    }

    /// <summary>Apaga as sessões mais antigas até o total caber em <see cref="MaxTotalBytes"/>. Nunca apaga a pasta em gravação.</summary>
    public static void Prune(string? keep = null)
    {
        var all = List();
        long total = all.Sum(i => i.Bytes);
        foreach (var old in all.OrderBy(i => i.Start))
        {
            if (total <= MaxTotalBytes) break;
            if (string.Equals(old.Folder, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(old.Folder, true); total -= old.Bytes; } catch { }
        }
    }
}
