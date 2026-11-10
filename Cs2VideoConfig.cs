using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>
/// Grava mudanças de vídeo no cs2_video.txt para testar perfis, com backup antes e restauração. Nunca altera resolução, proporção,
/// taxa de atualização nem monitor: o jogo roda em tela esticada e isso fica como está.
/// </summary>
public static class Cs2VideoConfig
{
    public static readonly string[] Protected =
    {
        "Version", "VendorID", "DeviceID",
        "setting.defaultres", "setting.defaultresheight", "setting.aspectratiomode",
        "setting.refreshrate_numerator", "setting.refreshrate_denominator", "setting.monitor_index",
    };

    public static string BackupFolder => Path.Combine(AppSettings.Folder, "cs2-backup");

    public static bool IsProtected(string key) => Protected.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Texto novo com as mudanças aplicadas; linhas desconhecidas e a ordem ficam como estavam. Chave protegida lança <see cref="ArgumentException"/>.</summary>
    public static string Apply(string videoText, IReadOnlyDictionary<string, string> changes)
    {
        foreach (var (key, value) in changes)
        {
            if (IsProtected(key)) throw new ArgumentException($"\"{key}\" é resolução/monitor e não pode ser alterado por aqui.");
            if (!Regex.IsMatch(key, @"^[A-Za-z0-9_.]+$") || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                throw new ArgumentException($"Valor inválido para \"{key}\".");
        }

        string text = videoText;
        foreach (var (key, value) in changes)
        {
            var rx = new Regex("(\"" + Regex.Escape(key) + "\"\\s+)\"[^\"]*\"", RegexOptions.IgnoreCase);
            if (rx.IsMatch(text)) text = rx.Replace(text, m => m.Groups[1].Value + "\"" + value + "\"", 1);
            else
            {
                int close = text.LastIndexOf('}');
                if (close < 0) throw new InvalidOperationException("cs2_video.txt em formato inesperado (sem chave de fechamento).");
                text = text.Insert(close, $"\t\"{key}\"\t\t\"{value}\"\n");
            }
        }
        return text;
    }

    public static bool Cs2Running()
    {
        var p = Process.GetProcessesByName("cs2");
        try { return p.Length > 0; } finally { foreach (var x in p) x.Dispose(); }
    }

    /// <summary>Faz o backup e grava. O jogo reescreve o arquivo ao fechar, então com o CS2 aberto a gravação é recusada.</summary>
    public static void ApplyToFile(string path, IReadOnlyDictionary<string, string> changes)
    {
        if (Cs2Running()) throw new InvalidOperationException("Feche o CS2: ele reescreve o cs2_video.txt ao fechar e desfaria a mudança.");
        string novo = Apply(File.ReadAllText(path), changes);
        Directory.CreateDirectory(BackupFolder);
        File.Copy(path, Path.Combine(BackupFolder, $"cs2_video.backup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.txt"));
        File.WriteAllText(path, novo);
    }

    /// <summary>Volta ao backup mais antigo (o estado de antes dos testes) e consome os backups. Sem backup, não faz nada. Devolve se restaurou.</summary>
    public static bool Restore(string path)
    {
        if (!Directory.Exists(BackupFolder)) return false;
        var backups = Directory.GetFiles(BackupFolder, "cs2_video.backup-*.txt").OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (backups.Count == 0) return false;
        if (Cs2Running()) throw new InvalidOperationException("Feche o CS2: ele reescreve o cs2_video.txt ao fechar.");
        File.Copy(backups[0], path, true);
        foreach (var b in backups) { try { File.Delete(b); } catch { } }
        return true;
    }

    /// <summary>Linhas "chave=valor" das opções que podem ser testadas (tudo menos as protegidas).</summary>
    public static List<string> Editable(IReadOnlyDictionary<string, string> video) =>
        video.Where(kv => !IsProtected(kv.Key)).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}={kv.Value}").ToList();

    /// <summary>Texto "chave=valor" por linha → mudanças. Linhas em branco são ignoradas; linha sem "=" lança <see cref="FormatException"/>.</summary>
    public static Dictionary<string, string> ParseChanges(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Linha sem \"=\": {line}");
            map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return map;
    }
}
