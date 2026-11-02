using System.IO;

namespace ResetExplode;

/// <summary>
/// Cache de shaders do driver de vídeo e do DirectX. Apagar faz os jogos recompilarem os shaders (as primeiras partidas
/// engasgam um pouco); vale depois de atualizar o driver, quando os engasgos aumentaram. Só o conteúdo das pastas
/// conhecidas é apagado; arquivos em uso (jogo aberto) ficam e entram em "Pulados".
/// </summary>
public static class ShaderCache
{
    public sealed record Location(string Name, string Path, long Bytes);

    public static List<Location> Locations()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string localLow = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var candidates = new (string Name, string Path)[]
        {
            ("NVIDIA DirectX", Path.Combine(local, @"NVIDIA\DXCache")),
            ("NVIDIA DirectX (por versão do driver)", Path.Combine(localLow, @"NVIDIA\PerDriverVersion\DXCache")),
            ("NVIDIA OpenGL/Vulkan", Path.Combine(local, @"NVIDIA\GLCache")),
            ("NVIDIA (sistema)", Path.Combine(programData, @"NVIDIA Corporation\NV_Cache")),
            ("AMD DirectX", Path.Combine(local, @"AMD\DxCache")),
            ("AMD DirectX 12", Path.Combine(local, @"AMD\DxcCache")),
            ("AMD DirectX 9", Path.Combine(local, @"AMD\Dx9Cache")),
            ("AMD OpenGL", Path.Combine(local, @"AMD\GLCache")),
            ("AMD Vulkan", Path.Combine(local, @"AMD\VkCache")),
            ("Intel", Path.Combine(local, @"Intel\ShaderCache")),
            ("Intel (LocalLow)", Path.Combine(localLow, @"Intel\ShaderCache")),
            ("DirectX (Windows)", Path.Combine(local, "D3DSCache")),
        };
        return candidates.Where(c => Directory.Exists(c.Path)).Select(c => new Location(c.Name, c.Path, Size(c.Path))).ToList();
    }

    public static long Size(string dir)
    {
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                try { total += f.Length; } catch { }
        }
        catch { }
        return total;
    }

    /// <summary>Apaga arquivos e subpastas vazias dentro de cada pasta (a pasta em si fica). Devolve bytes liberados e arquivos pulados.</summary>
    public static (long Freed, int Skipped) Clean(IEnumerable<string> dirs)
    {
        long freed = 0; int skipped = 0;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList())
            {
                try { long len = f.Length; f.Delete(); freed += len; }
                catch { skipped++; } // em uso por um jogo aberto, ou sem permissão
            }
            foreach (var sub in Directory.GetDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); } catch { }
        }
        return (freed, skipped);
    }
}
