using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>
/// Uso da GPU e VRAM por processo, pelos contadores do Windows "GPU Engine" e "GPU Process Memory" (os do Gerenciador
/// de Tarefas). Lidos a cada 1 s numa thread própria. Mostra quando outro programa (navegador com vídeo, gravação,
/// overlay) está disputando a placa de vídeo com o jogo.
/// </summary>
public sealed class GpuProcessProbe : SessionProbe
{
    private readonly object _lock = new();
    private Dictionary<int, (double Gpu, double VramMb)> _latest = new();
    private Thread? _thread;
    private volatile bool _stop;
    private int _pid;
    private double _othersSum; private int _othersN;

    public override IReadOnlyList<string> Columns { get; } = new[] { "jogo_gpu_pct", "jogo_vram_mb", "outros_gpu_pct" };

    /// <summary>Última leitura: pid → (% do motor mais ocupado da GPU, VRAM dedicada em MB).</summary>
    public Dictionary<int, (double Gpu, double VramMb)> Latest { get { lock (_lock) return _latest; } }

    public override void Start(int pid, string exe, string folder)
    {
        _pid = pid;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sessão: GPU por processo", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Loop()
    {
        if (Pdh.PdhOpenQuery(null, IntPtr.Zero, out var q) != 0) return;
        try
        {
            if (Pdh.PdhAddEnglishCounter(q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out var engine) != 0) return;
            Pdh.PdhAddEnglishCounter(q, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out var mem);
            Pdh.PdhCollectQueryData(q); // contador de taxa: a primeira coleta só serve de base
            while (!_stop)
            {
                Thread.Sleep(1000);
                if (Pdh.PdhCollectQueryData(q) != 0) continue;
                var byPid = Aggregate(Pdh.Read(engine), mem != IntPtr.Zero ? Pdh.Read(mem) : new());
                lock (_lock) _latest = byPid;
            }
        }
        finally { Pdh.PdhCloseQuery(q); }
    }

    private static readonly Regex EngineName = new(@"^pid_(\d+)_.*_engtype_(.+)$", RegexOptions.Compiled);
    private static readonly Regex MemName = new(@"^pid_(\d+)_", RegexOptions.Compiled);

    /// <summary>
    /// Como o Gerenciador de Tarefas: soma as instâncias de cada tipo de motor (3D, Copy, Compute...) por processo e
    /// fica com o tipo mais ocupado. VRAM: soma da memória dedicada do processo em todas as GPUs.
    /// </summary>
    public static Dictionary<int, (double Gpu, double VramMb)> Aggregate(List<(string Name, double Value)> engines, List<(string Name, double Value)> memory)
    {
        var perType = new Dictionary<(int Pid, string Type), double>();
        foreach (var (name, value) in engines)
        {
            var m = EngineName.Match(name);
            if (!m.Success) continue;
            var key = (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value);
            perType[key] = perType.GetValueOrDefault(key) + value;
        }
        var result = perType.GroupBy(kv => kv.Key.Pid).ToDictionary(g => g.Key, g => (Math.Min(100, g.Max(kv => kv.Value)), 0.0));
        foreach (var (name, value) in memory)
        {
            var m = MemName.Match(name);
            if (!m.Success) continue;
            int pid = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var cur = result.GetValueOrDefault(pid);
            result[pid] = (cur.Item1, cur.Item2 + value / 1048576.0);
        }
        return result;
    }

    public override void Fill(double?[] v, int o)
    {
        var latest = Latest;
        if (latest.Count == 0) return;
        if (latest.TryGetValue(_pid, out var game)) { v[o] = game.Gpu; v[o + 1] = game.VramMb; }
        double others = latest.Where(kv => kv.Key != _pid && kv.Key != 0).Sum(kv => kv.Value.Gpu);
        v[o + 2] = Math.Min(100, others);
        _othersSum += Math.Min(100, others); _othersN++;
    }

    public override Dictionary<string, object?> Finish()
    {
        _stop = true;
        _thread?.Join(2000);
        return new() { ["outros_gpu_medio_pct"] = _othersN > 0 ? Math.Round(_othersSum / _othersN, 1) : null };
    }

    public override void Dispose() { _stop = true; }
}

/// <summary>
/// A cada 5 s, os programas que mais usam CPU e GPU além do jogo (processos.csv), e a CPU somada de todos eles.
/// Inclui o próprio RESET EXPLODE, para mostrar quanto a gravação custa.
/// </summary>
public sealed class BackgroundProcessesProbe : SessionProbe
{
    private readonly GpuProcessProbe? _gpu;
    private Thread? _thread;
    private volatile bool _stop;
    private int _pid;
    private StreamWriter? _csv;
    private readonly Stopwatch _clock = new();
    private double? _othersCpu;
    // nome → (soma de CPU %, amostras, maior CPU %, maior RAM MB)
    private readonly Dictionary<string, (double Sum, int N, double Max, double Ram)> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private int _samples;

    public const int IntervalMs = 5000;

    public BackgroundProcessesProbe(GpuProcessProbe? gpu) => _gpu = gpu;

    public override IReadOnlyList<string> Columns { get; } = new[] { "outros_cpu_pct" };

    public override void Start(int pid, string exe, string folder)
    {
        _pid = pid;
        _csv = new StreamWriter(Path.Combine(folder, "processos.csv"), false, new UTF8Encoding(false));
        _csv.WriteLine("t_s,pid,processo,cpu_pct,ram_mb,gpu_pct,vram_mb");
        _clock.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sessão: processos", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Loop()
    {
        var prev = Snapshot();
        long prevT = Stopwatch.GetTimestamp();
        while (!_stop)
        {
            for (int i = 0; i < IntervalMs / 250 && !_stop; i++) Thread.Sleep(250);
            if (_stop) break;
            var cur = Snapshot();
            double wallMs = Stopwatch.GetElapsedTime(prevT).TotalMilliseconds;
            prevT = Stopwatch.GetTimestamp();
            var rows = Rows(prev, cur, wallMs, Environment.ProcessorCount, _pid, _gpu?.Latest ?? new());
            prev = cur;

            string t = _clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);
            lock (_lock)
            {
                _othersCpu = rows.Where(r => r.Pid != _pid && r.Pid != 0).Sum(r => r.Cpu);
                _samples++;
                // Por nome (Chrome, Discord... têm vários processos): soma dentro da amostra, depois média/máximo entre amostras.
                foreach (var g in rows.Where(r => r.Pid != _pid && r.Pid != 0).GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
                {
                    double cpu = g.Sum(r => r.Cpu), ram = g.Sum(r => r.RamMb);
                    var s = _stats.GetValueOrDefault(g.Key);
                    _stats[g.Key] = (s.Sum + cpu, s.N + 1, Math.Max(s.Max, cpu), Math.Max(s.Ram, ram));
                }
                // Só os relevantes no arquivo: 8 maiores em CPU + 3 maiores em GPU (sem repetir), fora o jogo e o "Idle".
                var top = rows.Where(r => r.Pid != _pid && r.Pid != 0).OrderByDescending(r => r.Cpu).Take(8)
                    .Concat(rows.Where(r => r.Pid != _pid && r.Pid != 0 && r.Gpu > 0).OrderByDescending(r => r.Gpu).Take(3))
                    .DistinctBy(r => r.Pid);
                foreach (var r in top)
                    _csv?.WriteLine(string.Join(',', t, r.Pid.ToString(CultureInfo.InvariantCulture), Csv(r.Name),
                        r.Cpu.ToString("0.0", CultureInfo.InvariantCulture), r.RamMb.ToString("0", CultureInfo.InvariantCulture),
                        r.Gpu.ToString("0.0", CultureInfo.InvariantCulture), r.VramMb.ToString("0", CultureInfo.InvariantCulture)));
                _csv?.Flush();
            }
        }
    }

    public sealed record ProcSample(int Pid, string Name, TimeSpan Cpu, double RamMb);
    public sealed record ProcRow(int Pid, string Name, double Cpu, double RamMb, double Gpu, double VramMb);

    private static Dictionary<int, ProcSample> Snapshot()
    {
        var d = new Dictionary<int, ProcSample>();
        foreach (var p in Process.GetProcesses())
        {
            try { d[p.Id] = new ProcSample(p.Id, p.ProcessName, p.TotalProcessorTime, p.WorkingSet64 / 1048576.0); }
            catch { /* processo protegido ou que acabou de fechar */ }
            finally { p.Dispose(); }
        }
        return d;
    }

    /// <summary>CPU % de cada processo entre duas fotografias (100% = todos os núcleos), com a GPU da última leitura.</summary>
    public static List<ProcRow> Rows(Dictionary<int, ProcSample> prev, Dictionary<int, ProcSample> cur, double wallMs, int cores, int gamePid,
                                     Dictionary<int, (double Gpu, double VramMb)> gpu)
    {
        var rows = new List<ProcRow>();
        if (wallMs <= 0) return rows;
        foreach (var c in cur.Values)
        {
            // Só compara o mesmo processo (o Windows reaproveita PIDs).
            if (!prev.TryGetValue(c.Pid, out var p) || p.Name != c.Name) continue;
            double cpu = Math.Max(0, (c.Cpu - p.Cpu).TotalMilliseconds / (wallMs * cores) * 100);
            var g = gpu.GetValueOrDefault(c.Pid);
            rows.Add(new ProcRow(c.Pid, c.Name, cpu, c.RamMb, g.Gpu, g.VramMb));
        }
        return rows;
    }

    public override void Fill(double?[] v, int o) { lock (_lock) v[o] = _othersCpu; }

    public override Dictionary<string, object?> Finish()
    {
        _stop = true;
        _thread?.Join(3000);
        lock (_lock)
        {
            _csv?.Dispose();
            _csv = null;
            string self = Process.GetCurrentProcess().ProcessName;
            // Média sobre todas as amostras (um programa que apareceu pouco não vira "o mais pesado").
            var heavy = _stats.Where(kv => !kv.Key.Equals(self, StringComparison.OrdinalIgnoreCase))
                .Select(kv => new { processo = kv.Key, cpu_medio_pct = Math.Round(kv.Value.Sum / Math.Max(1, _samples), 1),
                                    cpu_max_pct = Math.Round(kv.Value.Max, 1), ram_max_mb = Math.Round(kv.Value.Ram) })
                .OrderByDescending(x => x.cpu_medio_pct).Take(5).ToList();
            return new()
            {
                ["processos_mais_pesados"] = heavy,
                ["reset_explode_cpu_medio_pct"] = _stats.TryGetValue(self, out var me) ? Math.Round(me.Sum / Math.Max(1, _samples), 2) : null,
            };
        }
    }

    public override void Dispose() { _stop = true; lock (_lock) { _csv?.Dispose(); _csv = null; } }

    private static string Csv(string s) => s.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}

/// <summary>P/Invoke mínimo da PDH (contadores de desempenho do Windows) para contadores com várias instâncias.</summary>
internal static class Pdh
{
    private const uint FmtDouble = 0x00000200, FmtNoCap100 = 0x00008000, MoreData = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    /// <summary>Todas as instâncias do contador (nome, valor). Itens sem dado válido ficam de fora.</summary>
    public static List<(string Name, double Value)> Read(IntPtr counter)
    {
        var list = new List<(string, double)>();
        uint size = 0;
        if (PdhGetFormattedCounterArray(counter, FmtDouble | FmtNoCap100, ref size, out _, IntPtr.Zero) != MoreData || size == 0) return list;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, FmtDouble | FmtNoCap100, ref size, out uint count, buf) != 0) return list;
            // PDH_FMT_COUNTERVALUE_ITEM_W em x64: szName (8) + CStatus (4) + alinhamento (4) + double (8) = 24 bytes.
            const int itemSize = 24;
            for (int i = 0; i < count; i++)
            {
                IntPtr item = buf + i * itemSize;
                uint status = (uint)Marshal.ReadInt32(item + 8);
                if (status > 1) continue; // 0 = válido, 1 = dado novo
                string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? "";
                list.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16))));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }
}
