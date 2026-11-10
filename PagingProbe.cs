using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>
/// Memória do sistema a cada segundo pelos contadores do Windows (PDH, nomes em inglês para valer em qualquer idioma):
/// páginas lidas do disco por segundo (o PC buscando no arquivo de paginação o que não coube na RAM), cache em espera e commit.
/// Paginação alta no instante das quedas é o sinal direto de "a RAM não basta".
/// </summary>
public sealed class PagingProbe : SessionProbe
{
    private IntPtr _query, _pagesIn, _standbyNormal, _standbyReserve, _standbyCore, _commitPct;
    private double _sumPages, _maxPages; private int _n;

    public override IReadOnlyList<string> Columns { get; } = new[] { "paginas_entrada_s", "memoria_espera_mb", "memoria_commit_pct" };

    public override void Start(int pid, string exe, string folder)
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        Add(@"\Memory\Pages Input/sec", out _pagesIn);
        Add(@"\Memory\Standby Cache Normal Priority Bytes", out _standbyNormal);
        Add(@"\Memory\Standby Cache Reserve Bytes", out _standbyReserve);
        Add(@"\Memory\Standby Cache Core Bytes", out _standbyCore);
        Add(@"\Memory\% Committed Bytes In Use", out _commitPct);
        PdhCollectQueryData(_query); // contadores de taxa precisam de duas coletas
    }

    private void Add(string path, out IntPtr counter)
    {
        if (PdhAddEnglishCounter(_query, path, IntPtr.Zero, out counter) != 0) counter = IntPtr.Zero;
    }

    public override void Fill(double?[] v, int o)
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return;
        double? pages = Read(_pagesIn);
        v[o] = pages;
        double? sb = Sum(Read(_standbyNormal), Read(_standbyReserve), Read(_standbyCore));
        v[o + 1] = sb / 1048576.0;
        v[o + 2] = Read(_commitPct);
        if (pages is double p) { _sumPages += p; _maxPages = Math.Max(_maxPages, p); _n++; }
    }

    private static double? Sum(params double?[] xs) => xs.Any(x => x != null) ? xs.Sum(x => x ?? 0) : null;

    private static double? Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        return PdhGetFormattedCounterValue(counter, 0x00000200 /* PDH_FMT_DOUBLE */, out _, out var val) == 0 && val.CStatus == 0 ? val.Value : null;
    }

    public override Dictionary<string, object?> Finish() =>
        _n == 0 ? new() : new() { ["paginacao"] = new { media_paginas_s = Math.Round(_sumPages / _n, 1), pico_paginas_s = Math.Round(_maxPages) } };

    public override void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double Value; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
