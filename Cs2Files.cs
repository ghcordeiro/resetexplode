using System.IO;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>Arquivos do CS2 que o app lê (nunca o processo do jogo): versão (appmanifest), vídeo (cs2_video.txt), fps_max e pasta do console.</summary>
public static class Cs2Files
{
    public const string AppId = "730";

    public static string? ParseBuildId(string acfText)
    {
        try { return Vdf.Parse(acfText).Child("AppState")?.Get("buildid"); } catch { return null; }
    }

    /// <summary>Pares "setting.xxx" → valor do cs2_video.txt.</summary>
    public static Dictionary<string, string> ParseVideo(string cs2VideoText)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var node = Vdf.Parse(cs2VideoText).Child("video.cfg");
            if (node == null) return map;
            foreach (var kv in node.Items)
                if (kv.Value is string v) map[kv.Key] = v;
        }
        catch { }
        return map;
    }

    /// <summary>Último "fps_max N" de um texto de cfg ou de opções de inicialização ("+fps_max N"). <c>null</c> se não houver.</summary>
    public static string? ParseFpsMax(string text)
    {
        string? last = null;
        foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z_])\+?fps_max\s+(-?\d+)", RegexOptions.IgnoreCase))
            last = m.Groups[1].Value;
        return last;
    }

    /// <summary>Pasta "game" da instalação do CS2 (a que tem csgo\ e bin\), ou <c>null</c>.</summary>
    public static string? GameFolder()
    {
        try
        {
            if (SteamInfo.SteamPath() is not string steam) return null;
            foreach (var lib in SteamInfo.LibraryFolders(steam))
            {
                string game = Path.Combine(lib, "steamapps", "common", "Counter-Strike Global Offensive", "game");
                if (Directory.Exists(Path.Combine(game, "csgo"))) return game;
            }
        }
        catch { }
        return null;
    }

    public static string? Build()
    {
        try
        {
            if (SteamInfo.SteamPath() is not string steam) return null;
            foreach (var lib in SteamInfo.LibraryFolders(steam))
            {
                string acf = Path.Combine(lib, "steamapps", $"appmanifest_{AppId}.acf");
                if (File.Exists(acf)) return ParseBuildId(File.ReadAllText(acf));
            }
        }
        catch { }
        return null;
    }

    /// <summary>cs2_video.txt mais recente entre as contas da Steam deste PC (userdata\&lt;id&gt;\730\local\cfg).</summary>
    public static string? VideoPath()
    {
        try
        {
            if (SteamInfo.SteamPath() is not string steam) return null;
            string ud = Path.Combine(steam, "userdata");
            if (!Directory.Exists(ud)) return null;
            return Directory.GetDirectories(ud).Select(d => Path.Combine(d, AppId, "local", "cfg", "cs2_video.txt")).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>fps_max efetivo: opções de inicialização da Steam, senão o autoexec.cfg.</summary>
    public static string? FpsMax()
    {
        try
        {
            if (SteamInfo.GetLaunchOptions(AppId) is { Length: > 0 } opts && ParseFpsMax(opts) is string a) return a;
            if (GameFolder() is string game)
            {
                string auto = Path.Combine(game, "csgo", "cfg", "autoexec.cfg");
                if (File.Exists(auto)) return ParseFpsMax(File.ReadAllText(auto));
            }
        }
        catch { }
        return null;
    }
}
