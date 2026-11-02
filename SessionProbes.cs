using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>
/// Fonte extra de métricas de uma sessão de jogo (processo do jogo, link PCIe, GPU por processo, rede, PresentMon, mouse...).
/// Cada sonda declara as colunas que acrescenta ao sensores.csv; <see cref="Fill"/> roda 1 vez por segundo na thread da tela
/// e deve ser barata: trabalho pesado fica numa thread da própria sonda, que só deixa o último valor pronto.
/// </summary>
public abstract class SessionProbe : IDisposable
{
    public abstract IReadOnlyList<string> Columns { get; }

    /// <summary>Começa a coletar. <paramref name="folder"/> é a pasta da sessão, para arquivos próprios da sonda.</summary>
    public virtual void Start(int pid, string exe, string folder) { }

    /// <summary>Escreve <see cref="Columns"/>.Count valores a partir de values[offset] (null = sem leitura).</summary>
    public abstract void Fill(double?[] values, int offset);

    /// <summary>Fim da sessão: itens que vão para o resumo.json.</summary>
    public virtual Dictionary<string, object?> Finish() => new();

    public virtual void Dispose() { }
}

/// <summary>CPU, memória e threads do processo do jogo (a E/S entra na Fase 2).</summary>
public sealed class GameProcessProbe : SessionProbe
{
    private Process? _p;
    private TimeSpan _cpu0;
    private long _t0;

    public override IReadOnlyList<string> Columns { get; } = new[] { "jogo_cpu_pct", "jogo_ram_mb", "jogo_commit_mb", "jogo_threads" };

    public override void Start(int pid, string exe, string folder)
    {
        _p = Process.GetProcessById(pid);
        try { _cpu0 = _p.TotalProcessorTime; } catch { }
        _t0 = Stopwatch.GetTimestamp();
    }

    public override void Fill(double?[] v, int o)
    {
        if (_p == null) return;
        try
        {
            _p.Refresh();
            var cpu = _p.TotalProcessorTime;
            double wallMs = Stopwatch.GetElapsedTime(_t0).TotalMilliseconds;
            if (wallMs > 0) v[o] = 100.0 * (cpu - _cpu0).TotalMilliseconds / (wallMs * Environment.ProcessorCount);
            _cpu0 = cpu; _t0 = Stopwatch.GetTimestamp();
            v[o + 1] = _p.WorkingSet64 / 1048576.0;
            v[o + 2] = _p.PrivateMemorySize64 / 1048576.0;
            v[o + 3] = _p.Threads.Count;
        }
        catch { /* anti-cheat pode negar acesso ao processo: fica sem essas colunas */ }
    }

    public override void Dispose() => _p?.Dispose();
}

/// <summary>
/// Link PCIe da placa de vídeo (NVML) a cada 10 s. Em repouso a placa baixa o link para economizar energia;
/// o que importa é o maior link visto com o jogo rodando.
/// </summary>
public sealed class PcieProbe : SessionProbe
{
    private readonly IntPtr _nvml;
    private int _tick;
    private uint? _gen, _width;
    public uint? MaxGen { get; private set; }
    public uint? MaxWidth { get; private set; }

    public PcieProbe(IntPtr nvml) => _nvml = nvml;

    public override IReadOnlyList<string> Columns { get; } = new[] { "gpu_pcie_geracao", "gpu_pcie_largura" };

    public override void Fill(double?[] v, int o)
    {
        if (_nvml == IntPtr.Zero) return;
        if (_tick++ % 10 == 0)
        {
            (_gen, _width) = SessionContextCollector.PcieNow(_nvml);
            if (_gen is uint g) MaxGen = Math.Max(MaxGen ?? 0, g);
            if (_width is uint w) MaxWidth = Math.Max(MaxWidth ?? 0, w);
        }
        v[o] = _gen; v[o + 1] = _width;
    }
}
