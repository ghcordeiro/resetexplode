using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ResetExplode;

/// <summary>Uma execução de um perfil de teste, lida da pasta da sessão.</summary>
public sealed record RunRecord(string Folder, bool Valid, bool WarmUp, double Low1Fps, double Low01Fps, double AvgFps,
                               double StdDevMs, int Drops, double AltTabMs, string? Invalid,
                               double DisplayLatMs = 0, double PcLatMs = 0, double ClickLatMs = 0);

/// <summary>
/// Perfil de teste ("linha de base", "fps_max 240"...): o nome é gravado em perfil.txt assim que a sessão começa
/// (sobrevive a um travamento do PC) e em resumo.json no fim. Sessão com perfil nunca é apagada pela limpeza de espaço.
/// </summary>
public static class SessionProfile
{
    public const string FileName = "perfil.txt";

    /// <summary>Nome seguro para arquivo e comparação: apara, troca caracteres inválidos por "-" e corta em 60. <c>null</c> se vazio.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = string.Concat(raw.Trim().Select(c => "/\\:*?\"<>|".Contains(c) || char.IsControl(c) ? '-' : c)).Trim();
        if (s.Length > 60) s = s[..60].TrimEnd();
        return s.Length == 0 ? null : s;
    }

    /// <summary>Grava o perfil na pasta da sessão (chamado no começo da gravação).</summary>
    public static void Write(string sessionFolder, string profile)
    {
        try { File.WriteAllText(Path.Combine(sessionFolder, FileName), profile); } catch { }
    }

    /// <summary>Perfil da sessão, ou <c>null</c>. Vale perfil.txt; se faltar, o campo "perfil" do resumo.json.</summary>
    public static string? Read(string sessionFolder)
    {
        try
        {
            string p = Path.Combine(sessionFolder, FileName);
            if (File.Exists(p)) return Normalize(File.ReadAllText(p));
            string r = Path.Combine(sessionFolder, "resumo.json");
            if (!File.Exists(r)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(r));
            return doc.RootElement.TryGetProperty("perfil", out var e) && e.ValueKind == JsonValueKind.String ? Normalize(e.GetString()) : null;
        }
        catch { return null; }
    }

    public static bool IsPinned(string sessionFolder) => Read(sessionFolder) != null;

    /// <summary>Perfis que existem, do mais recente ao mais antigo.</summary>
    public static List<string> AllProfiles()
    {
        var found = new List<(string Name, DateTime When)>();
        if (!Directory.Exists(GameSession.Root)) return new List<string>();
        foreach (var dir in Directory.GetDirectories(GameSession.Root))
            if (Read(dir) is string p) found.Add((p, Directory.GetCreationTime(dir)));
        return found.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(x => x.When)).Select(g => g.Key).ToList();
    }

    /// <summary>Execuções do perfil, incluindo as que não têm resumo (viram inválidas: "sem resumo"). Mais antigas primeiro.</summary>
    public static List<RunRecord> Runs(string profile)
    {
        var list = new List<(DateTime When, RunRecord Run)>();
        if (!Directory.Exists(GameSession.Root)) return new List<RunRecord>();
        foreach (var dir in Directory.GetDirectories(GameSession.Root))
        {
            if (!string.Equals(Read(dir), profile, StringComparison.OrdinalIgnoreCase)) continue;
            DateTime when = Directory.GetCreationTime(dir);
            string resumo = Path.Combine(dir, "resumo.json");
            if (!File.Exists(resumo)) { list.Add((when, new RunRecord(dir, false, false, 0, 0, 0, 0, 0, 0, "sem resumo"))); continue; }
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(resumo));
                var r = doc.RootElement;
                double D(string k) => r.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
                bool B(string k, bool def) => r.TryGetProperty(k, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : def;
                string? reason = r.TryGetProperty("motivo_invalida", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                // Latências do PresentMon (0 = sem dado): até a tela, do PC (entrada → tela) e clique até a imagem.
                double N(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDouble() : 0;
                var ex = r.TryGetProperty("exibicao", out var exv) ? exv : default;
                double dispLat = ex.ValueKind == JsonValueKind.Object && ex.TryGetProperty("latencia_exibicao_ms", out var le) ? N(le, "media") : 0;
                double pcLat = N(ex, "latencia_pc_ms_media");
                double clickLat = N(ex, "latencia_clique_amostras") >= 5 ? N(ex, "latencia_clique_ms_media") : 0;
                if (B("profundo", false)) continue; // diagnóstico profundo pesa no jogo: fica fora de toda comparação
                bool valid = B("valida", true) && D("frames") > 0;
                if (D("frames") <= 0) reason ??= "sem quadros";
                list.Add((when, new RunRecord(dir, valid, B("aquecimento", false), D("low_1pct_fps"), D("low_01pct_fps"), D("fps_medio"),
                    D("desvio_padrao_ms"), (int)D("quedas"), D("alttab_mediana_ms"), valid ? null : reason, dispLat, pcLat, clickLat)));
            }
            catch { list.Add((when, new RunRecord(dir, false, false, 0, 0, 0, 0, 0, 0, "resumo ilegível"))); }
        }
        return list.OrderBy(x => x.When).Select(x => x.Run).ToList();
    }
}
