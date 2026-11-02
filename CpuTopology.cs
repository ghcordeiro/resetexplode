using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>
/// Núcleos e caches L3 da CPU (GetLogicalProcessorInformationEx), para prender o jogo nos núcleos certos:
/// Ryzen X3D de 2 CCDs (o CCD com 3D V-Cache tem o L3 maior) e Intel híbridos (P-cores têm classe de eficiência maior).
/// Só o grupo 0 de processadores (até 64 threads), que é o caso de qualquer PC de jogo.
/// </summary>
public static class CpuTopology
{
    public sealed record Core(byte EfficiencyClass, ulong Mask);
    public sealed record L3(uint SizeBytes, ulong Mask);
    public sealed record Info(List<Core> Cores, List<L3> Caches);

    public const string ModeVCache = "vcache", ModePCores = "pcores";

    public static Info Read()
    {
        var info = new Info(new(), new());
        uint len = 0;
        GetLogicalProcessorInformationEx(0xFFFF /* RelationAll */, IntPtr.Zero, ref len);
        if (len == 0) return info;
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(0xFFFF, buf, ref len)) return info;
            for (int off = 0; off < len;)
            {
                IntPtr p = buf + off;
                int rel = Marshal.ReadInt32(p), size = Marshal.ReadInt32(p + 4);
                if (size <= 0) break;
                // Deslocamentos de SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX em x64 (união começa em +8):
                // núcleo: EfficiencyClass +9, GROUP_AFFINITY +32 (Mask +32, Group +40);
                // cache: Level +8, CacheSize +12, Type +16, GROUP_AFFINITY +40 (Mask +40, Group +48).
                if (rel == 0 && Marshal.ReadInt16(p + 40) == 0)
                    info.Cores.Add(new Core(Marshal.ReadByte(p + 9), (ulong)Marshal.ReadInt64(p + 32)));
                else if (rel == 2 && Marshal.ReadByte(p + 8) == 3 && Marshal.ReadInt32(p + 16) is 0 or 2 && Marshal.ReadInt16(p + 48) == 0)
                    info.Caches.Add(new L3((uint)Marshal.ReadInt32(p + 12), (ulong)Marshal.ReadInt64(p + 40)));
                off += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return info;
    }

    /// <summary>Máscara de afinidade para o modo pedido, ou null se a CPU não tem esse tipo de núcleo.</summary>
    public static ulong? MaskFor(Info info, string? mode) => mode switch
    {
        ModeVCache when info.Caches.Select(c => c.SizeBytes).Distinct().Count() > 1 => info.Caches.MaxBy(c => c.SizeBytes)!.Mask,
        ModePCores when info.Cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1 =>
            info.Cores.Where(c => c.EfficiencyClass == info.Cores.Max(x => x.EfficiencyClass)).Aggregate(0UL, (m, c) => m | c.Mask),
        _ => null,
    };

    public static string Describe(Info info)
    {
        if (info.Cores.Count == 0) return "Topologia da CPU indisponível.";
        if (MaskFor(info, ModeVCache) is ulong v)
        {
            var big = info.Caches.MaxBy(c => c.SizeBytes)!;
            return $"{info.Caches.Count} blocos de núcleos (CCDs); o 3D V-Cache ({big.SizeBytes >> 20} MB de L3) está nas threads {MaskText(v)}.";
        }
        if (MaskFor(info, ModePCores) is ulong pc)
        {
            int p = info.Cores.Count(c => (c.Mask & pc) != 0);
            return $"CPU híbrida: {p} núcleos de desempenho (P-cores, threads {MaskText(pc)}) e {info.Cores.Count - p} de eficiência (E-cores).";
        }
        string l3 = info.Caches.Count > 0 ? $" e {info.Caches.Count} bloco(s) de cache L3 de {info.Caches[0].SizeBytes >> 20} MB" : "";
        return $"{info.Cores.Count} núcleos iguais{l3}: prender o jogo em núcleos não ajuda nesta CPU.";
    }

    /// <summary>0b1111_0000_1111 → "0-3,8-11".</summary>
    public static string MaskText(ulong mask)
    {
        var parts = new List<string>();
        for (int i = 0; i < 64; i++)
        {
            if ((mask >> i & 1) == 0) continue;
            int j = i;
            while (j + 1 < 64 && (mask >> (j + 1) & 1) == 1) j++;
            parts.Add(i == j ? $"{i}" : $"{i}-{j}");
            i = j;
        }
        return parts.Count > 0 ? string.Join(',', parts) : "nenhuma";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
