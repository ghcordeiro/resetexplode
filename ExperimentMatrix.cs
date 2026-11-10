using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ResetExplode;

/// <summary>Um experimento da matriz: um perfil de teste com UMA mudança em relação à linha de base.</summary>
public sealed class MatrixStep
{
    public int Order { get; set; }
    public string Profile { get; set; } = "";
    public string Change { get; set; } = "";
    public string Hypothesis { get; set; } = "";
    /// <summary>"base" (a própria linha de base), "teste" (muda uma coisa), "info" (medição que não vira linha de base) ou "compra".</summary>
    public string Kind { get; set; } = "teste";
    /// <summary>pendente | medindo | venceu | perdeu | ruído | medido | pulado</summary>
    public string Status { get; set; } = "pendente";
    public string? Result { get; set; }
    /// <summary>Opções de vídeo do CS2 que o passo aplica (opcional; ideia 13).</summary>
    public Dictionary<string, string>? Video { get; set; }
    /// <summary>Valores de vídeo de antes do passo, para REVERTER.</summary>
    public Dictionary<string, string>? VideoBefore { get; set; }

    public bool Open => Status is "pendente" or "medindo";
    public string Line => $"{Mark} {Order}. {Profile} — {Change}" + (Result != null ? $"  ·  {Result}" : "");
    private string Mark => Status switch { "venceu" => "✔", "perdeu" => "✘", "ruído" => "≈", "medido" or "base" => "•", "pulado" => "–", "medindo" => "▶", _ => "○" };
}

/// <summary>
/// Matriz guiada (PRD): uma variável por vez, 3 execuções válidas por perfil, e o perfil que vence além do ruído vira a nova linha de base.
/// Fica em %AppData%\ResetExplode\matriz.json e pode ser editada à mão.
/// </summary>
public sealed class ExperimentMatrix
{
    public string? BaselineProfile { get; set; }
    public List<MatrixStep> Steps { get; set; } = new();

    private static string FilePath => Path.Combine(AppSettings.Folder, "matriz.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ExperimentMatrix Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ExperimentMatrix>(File.ReadAllText(FilePath), Json) ?? Default(); }
        catch { }
        return Default();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    public static ExperimentMatrix Default()
    {
        var m = new ExperimentMatrix();
        void S(string profile, string change, string why, string kind = "teste") =>
            m.Steps.Add(new MatrixStep { Order = m.Steps.Count, Profile = profile, Change = change, Hypothesis = why, Kind = kind });
        S("linha de base", "nada: a configuração de hoje (PBO + CO -20, XMP 3200, fps livre)", "referência", "base");
        S("bios atualizada", "BIOS/AGESA mais recente, mesmas configurações (salve um OC Profile antes)", "stutter do fTPM");
        S("fps_max 240", "fps_max 240 (opções de inicialização ou autoexec)", "fps sem limite");
        S("gsync reflex", "G-Sync + V-Sync no driver (Biblioteca › NVIDIA) + Reflex no jogo, sem fps_max", "fps sem limite");
        S("co -15", "Curve Optimizer -15 e boost +100 MHz", "clock stretching com -20");
        S("rebar", "Above 4G Decoding e Re-Size BAR ligados na BIOS", "ganho de GPU");
        S("ram 3600", "RAM a 3600 MT/s com timings ajustados, depois de 30 min de stress de RAM sem erro", "latência da RAM");
        S("uso real", "o melhor perfil até aqui, com os programas do dia a dia abertos (Discord em call, navegador)", "quantidade de RAM", "info");
        S("32gb", "2x16 GB (só se \"vale comprar?\" disser que sim)", "quantidade de RAM", "compra");
        S("5500x3d", "troca do processador (só se \"vale comprar?\" disser que sim)", "cache L3", "compra");
        return m;
    }

    /// <summary>Opções de vídeo do CS2 que pesam na CPU, com o valor "mais leve". Só viram passo se a chave existe no cs2_video.txt deste PC
    /// e o valor atual é outro (nome errado aqui só faz o passo não aparecer).</summary>
    public static readonly (string Key, string Light, string Title)[] VideoCandidates =
    {
        ("setting.videocfg_dynamic_shadows", "0", "sombras dinâmicas desligadas (perde a sombra de inimigos)"),
        ("setting.videocfg_particle_detail", "0", "detalhe de partículas no mínimo"),
        ("setting.videocfg_shadow_quality", "0", "qualidade de sombras no mínimo"),
        ("setting.videocfg_ao_detail", "0", "oclusão de ambiente desligada"),
        ("setting.r_player_visibility_mode", "0", "Boost Player Contrast desligado (custa visibilidade; teste só pelo fps)"),
        ("setting.shaderquality", "0", "qualidade de shaders baixa"),
        ("setting.msaa_samples", "0", "MSAA desligado"),
    };

    /// <summary>Acrescenta (antes das compras) um passo para cada opção de vídeo que pode ficar mais leve. Devolve quantos entraram.</summary>
    public int AddVideoSteps(IReadOnlyDictionary<string, string> video)
    {
        int added = 0;
        foreach (var (key, light, title) in VideoCandidates)
        {
            if (!video.TryGetValue(key, out var cur) || cur == light) continue;
            string profile = "video " + key.Replace("setting.", "").Replace("videocfg_", "").Replace('_', ' ');
            if (Steps.Any(x => x.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase))) continue;
            Steps.Add(new MatrixStep { Profile = profile, Change = $"vídeo: {title} ({key} {cur} → {light}; o app grava)", Hypothesis = "vídeo que pesa na CPU",
                                       Video = new() { [key] = light } });
            added++;
        }
        // compras sempre por último
        int order = 0;
        foreach (var x in Steps.OrderBy(x => x.Kind == "compra" ? 1 : 0).ThenBy(x => x.Order)) x.Order = order++;
        Steps = Steps.OrderBy(x => x.Order).ToList();
        return added;
    }

    public MatrixStep? Current => Steps.Where(s => s.Open && s.Kind != "compra").OrderBy(s => s.Order).FirstOrDefault();

    /// <summary>Reavalia o passo do perfil (chamado depois de cada captura). Devolve a frase para a tela, ou null se o perfil não é da matriz.</summary>
    public string? Evaluate(string profile)
    {
        var step = Steps.FirstOrDefault(s => s.Profile.Equals(profile, StringComparison.OrdinalIgnoreCase) && s.Open);
        if (step == null) return null;
        var runs = SessionProfile.Runs(step.Profile);
        int valid = runs.Count(r => r.Valid && !r.WarmUp);
        if (valid < ProfileComparer.MinRuns)
        {
            step.Status = "medindo";
            step.Result = $"{valid}/{ProfileComparer.MinRuns} execuções";
            Save();
            return $"Matriz: \"{step.Profile}\" com {valid} de {ProfileComparer.MinRuns} execuções válidas.";
        }

        string msg;
        if (step.Kind == "base" || BaselineProfile == null)
        {
            BaselineProfile = step.Profile;
            step.Status = "base";
            step.Result = $"1% low {ProfileComparer.Summarize("", runs.Where(r => r.Valid && !r.WarmUp).Select(r => r.Low1Fps)).Median:0} fps";
            msg = $"Matriz: linha de base medida. Próximo passo: {Current?.Profile ?? "nenhum"}.";
        }
        else if (step.Kind == "info")
        {
            step.Status = "medido";
            step.Result = "use VALE COMPRAR? para a decisão de RAM";
            msg = $"Matriz: \"{step.Profile}\" medido. Agora clique em VALE COMPRAR?.";
        }
        else
        {
            var rows = ProfileComparer.CompareRuns(SessionProfile.Runs(BaselineProfile), runs);
            var low = rows[0];
            var dev = rows.First(r => r.Baseline.Name.StartsWith("Desvio"));
            bool better = low.Verdict == Verdict.Better || (low.Verdict == Verdict.WithinNoise && dev.Verdict == Verdict.Better);
            string pct = low.DiffPct.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture) + "%";
            if (better)
            {
                step.Status = "venceu";
                step.Result = low.Verdict == Verdict.Better ? $"1% low {pct}: vira a linha de base" : $"1% low igual, frametime mais estável: vira a linha de base";
                msg = $"Matriz: \"{step.Profile}\" venceu \"{BaselineProfile}\" ({step.Result}). Mantenha a mudança.";
                BaselineProfile = step.Profile;
            }
            else if (low.Verdict == Verdict.Worse)
            {
                step.Status = "perdeu";
                step.Result = $"1% low {pct}: reverta";
                msg = $"Matriz: \"{step.Profile}\" ficou pior ({pct}). Reverta: {(step.VideoBefore != null ? "clique em REVERTER" : step.Change)}.";
            }
            else
            {
                step.Status = "ruído";
                step.Result = $"dentro do ruído ({pct}): reverta";
                msg = $"Matriz: \"{step.Profile}\" ficou dentro do ruído. Reverta: {(step.VideoBefore != null ? "clique em REVERTER" : step.Change)}.";
            }
        }
        Save();
        return msg + (Current is { } next ? $" Próximo: \"{next.Profile}\" — {next.Change}." : " Matriz concluída: clique em VALE COMPRAR?.");
    }
}
