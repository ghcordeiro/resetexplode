using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResetExplode;

/// <summary>
/// Diagnóstico profundo: eventos de DPC e ISR do kernel (ETW) para achar qual driver atrasa o sistema na hora das quedas.
/// Pesa no jogo (muitos eventos), então a sessão fica marcada <c>profundo</c> e fora de toda comparação.
/// Só lê eventos do Windows; nunca toca o processo do jogo. Se a sessão do kernel já estiver em uso (outra ferramenta de ETW),
/// a sonda não começa e a sessão registra o motivo.
/// </summary>
public sealed class DpcProbe : SessionProbe
{
    private const double MinMs = 0.5; // abaixo disso o driver não atrapalha o frame
    private TraceEventSession? _session;
    private Thread? _thread;
    private DateTime _start;
    private string _folder = "";
    private readonly ConcurrentQueue<(DateTime When, ulong Routine, double Ms, string Kind)> _spikes = new();
    private readonly List<(ulong Base, ulong Size, string Name)> _images = new();
    private readonly object _imgLock = new();
    private string? _error;
    // Thread principal: tempo de CPU de cada thread do jogo por segundo (troca de contexto do kernel).
    private int _gamePid;
    private readonly Dictionary<int, (int Tid, int Pid, DateTime Since)> _onCpu = new();
    private readonly Dictionary<int, Dictionary<int, double>> _threadMs = new(); // segundo → thread → ms
    private readonly object _csLock = new();

    public override IReadOnlyList<string> Columns { get; } = Array.Empty<string>();

    public override void Start(int pid, string exe, string folder)
    {
        _folder = folder;
        _start = DateTime.Now;
        try
        {
            _gamePid = pid;
            _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName) { StopOnDispose = true, BufferSizeMB = 128 };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.DeferedProcedureCalls | KernelTraceEventParser.Keywords.Interrupt |
                                          KernelTraceEventParser.Keywords.ImageLoad | KernelTraceEventParser.Keywords.ContextSwitch);
            var k = _session.Source.Kernel;
            k.PerfInfoDPC += d => { if (d.ElapsedTimeMSec >= MinMs) _spikes.Enqueue((d.TimeStamp, (ulong)d.Routine, d.ElapsedTimeMSec, "DPC")); };
            k.PerfInfoThreadedDPC += d => { if (d.ElapsedTimeMSec >= MinMs) _spikes.Enqueue((d.TimeStamp, (ulong)d.Routine, d.ElapsedTimeMSec, "DPC")); };
            k.PerfInfoTimerDPC += d => { if (d.ElapsedTimeMSec >= MinMs) _spikes.Enqueue((d.TimeStamp, (ulong)d.Routine, d.ElapsedTimeMSec, "DPC")); };
            k.ThreadCSwitch += d => OnSwitch(d.ProcessorNumber, d.TimeStamp, d.NewThreadID, d.NewProcessID);
            k.ImageLoad += d => { lock (_imgLock) _images.Add(((ulong)d.ImageBase, (ulong)d.ImageSize, Path.GetFileName(d.FileName))); };
            k.ImageDCStart += d => { lock (_imgLock) _images.Add(((ulong)d.ImageBase, (ulong)d.ImageSize, Path.GetFileName(d.FileName))); };
            _thread = new Thread(() => { try { _session.Source.Process(); } catch (Exception ex) { _error = ex.Message; } })
            { IsBackground = true, Name = "ETW DPC" };
            _thread.Start();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            try { _session?.Dispose(); } catch { }
            _session = null;
        }
    }

    private void OnSwitch(int cpu, DateTime when, int newTid, int newPid)
    {
        lock (_csLock)
        {
            if (_onCpu.TryGetValue(cpu, out var cur) && cur.Pid == _gamePid && when > cur.Since)
            {
                int sec = (int)Math.Ceiling((when - _start).TotalSeconds);
                if (!_threadMs.TryGetValue(sec, out var bySec)) _threadMs[sec] = bySec = new Dictionary<int, double>();
                bySec[cur.Tid] = bySec.GetValueOrDefault(cur.Tid) + (when - cur.Since).TotalMilliseconds;
            }
            _onCpu[cpu] = (newTid, newPid, when);
        }
    }

    public override void Fill(double?[] values, int offset) { }

    public override Dictionary<string, object?> Finish()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        try { _thread?.Join(2000); } catch { }
        if (_error != null && _spikes.IsEmpty)
            return new() { ["profundo"] = true, ["dpc_isr"] = new { erro = _error } };

        var list = _spikes.ToArray();
        List<(ulong Base, ulong Size, string Name)> images;
        lock (_imgLock) images = _images.ToList();
        string Driver(ulong addr)
        {
            foreach (var im in images) if (addr >= im.Base && addr < im.Base + im.Size) return im.Name;
            return "desconhecido";
        }

        var lines = new List<string> { "t_s,tipo,driver,atraso_us" };
        foreach (var s in list.OrderBy(x => x.When))
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{(s.When - _start).TotalSeconds:0.000},{s.Kind},{Driver(s.Routine)},{s.Ms * 1000:0}"));
        try { File.WriteAllLines(Path.Combine(_folder, "dpc.csv"), lines, new UTF8Encoding(false)); } catch { }

        // threads.csv: por segundo, % de um núcleo usado pela thread mais ocupada do jogo (100% = a thread não parou nenhum instante)
        Dictionary<int, Dictionary<int, double>> perSec;
        lock (_csLock) perSec = _threadMs.ToDictionary(kv => kv.Key, kv => kv.Value);
        var topPct = new List<double>();
        var tl = new List<string> { "t_s,thread_principal_pct,thread_id,threads_ativas" };
        foreach (var (sec, threads) in perSec.OrderBy(kv => kv.Key))
        {
            var best = threads.OrderByDescending(kv => kv.Value).First();
            double pct = Math.Min(100, best.Value / 10.0);
            topPct.Add(pct);
            tl.Add(string.Create(CultureInfo.InvariantCulture, $"{sec},{pct:0.#},{best.Key},{threads.Count(kv => kv.Value > 10)}"));
        }
        try { if (topPct.Count > 0) File.WriteAllLines(Path.Combine(_folder, "threads.csv"), tl, new UTF8Encoding(false)); } catch { }
        object? threadSummary = topPct.Count == 0 ? null : new
        {
            media_pct = Math.Round(topPct.Average(), 1),
            segundos_acima_90_pct = Math.Round(100.0 * topPct.Count(x => x >= 90) / topPct.Count),
        };

        var top = list.GroupBy(s => Driver(s.Routine))
            .Select(g => new { driver = g.Key, picos = g.Count(), maximo_us = Math.Round(g.Max(x => x.Ms) * 1000), total_ms = Math.Round(g.Sum(x => x.Ms), 1) })
            .OrderByDescending(x => x.total_ms).Take(5).ToList();
        return new() { ["profundo"] = true, ["dpc_isr"] = new { picos = list.Length, limite_ms = MinMs, drivers = top }, ["thread_principal"] = threadSummary };
    }

    public override void Dispose() { try { _session?.Dispose(); } catch { } }
}
