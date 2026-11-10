using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>O que se mediu numa sessão e importa para as regras. Campo <c>null</c> = sem dado: a regra que depende dele não dispara.</summary>
public sealed record DiagFacts(
    double? GpuLoadPctAtLowFps, double? BusiestThreadPctAtLowFps, double? RamUsedPct, int? RamMts, bool Ryzen5000, int? BiosAgeMonths,
    string? BiosDate, double? DropRegularity, double AvgFps, double Low1Fps, int MonitorHz, double FrametimeStdDevMs, bool FpsLimited,
    double? ClockDropPctUnderLoad, double? CpuTempMax, bool SecondMonitorOtherHz, double? IndependentFlipPct, string? BackgroundCulprit,
    double? PagingAtDropsPct = null, double? PagingPeak = null, double? SystemWaitDropPct = null, double? CpuDropPct = null);

/// <summary>Uma sugestão de experimento com a evidência que a motivou. <paramref name="Cost"/> = exige comprar algo.</summary>
public sealed record Suggestion(string Rule, string Evidence, string Action, bool Cost)
{
    public string Text => $"{Rule}: {Evidence} Sugestão: {Action}" + (Cost ? " (envolve compra: só depois de esgotar os ajustes grátis)" : "");
}

/// <summary>
/// Regras do diagnóstico, na ordem da spec. Cada uma cita o número medido e propõe um experimento a MEDIR, nunca uma promessa.
/// Os limiares são pontos de partida: calibrar com as primeiras sessões reais.
/// </summary>
public static class DiagnosisRules
{
    public const double CpuLimitGpuBelow = 90, CpuLimitThreadAbove = 90, MemoryAbove = 85, PeriodicBelow = 0.15,
                        ClockDropAbove = 3, ClockDropTempBelow = 80, UnlimitedFpsFactor = 1.5, UnlimitedStdDevMs = 1.0, FlipBelow = 80;

    public static List<Suggestion> Evaluate(DiagFacts f)
    {
        var list = new List<Suggestion>();
        string N0(double v) => v.ToString("0", CultureInfo.CurrentCulture);

        bool cpuLimit = (f.GpuLoadPctAtLowFps < CpuLimitGpuBelow && f.BusiestThreadPctAtLowFps > CpuLimitThreadAbove)
                        || (f.CpuDropPct >= 60 && f.GpuLoadPctAtLowFps < CpuLimitGpuBelow);
        if (cpuLimit)
        {
            list.Add(new("Limite de CPU em poucos threads",
                $"nos segundos de fps mais baixo a GPU estava em {N0(f.GpuLoadPctAtLowFps ?? 0)}% e o thread mais ocupado em {N0(f.BusiestThreadPctAtLowFps ?? 0)}%" +
                (f.CpuDropPct is double cd ? $"; {N0(cd)}% das quedas foram com a CPU ocupada." : " (o uso total da CPU esconde isso)."),
                "testar um limite de fps, fechar programas em segundo plano e RAM mais rápida; cada um como perfil separado.", false));
            if (f.Low1Fps < f.MonitorHz)
                list.Add(new("Cache L3 do processador",
                    $"o 1% low ({N0(f.Low1Fps)} fps) ficou abaixo da taxa do monitor ({f.MonitorHz} Hz) com a CPU como limite.",
                    "se os ajustes grátis não levarem o 1% low até a taxa do monitor, um processador com mais cache L3 (X3D) é a compra a avaliar.", true));
        }

        if (f.RamUsedPct > MemoryAbove)
            list.Add(new("Memória quase cheia", $"a RAM chegou a {N0(f.RamUsedPct!.Value)}% de uso.",
                "medir com os programas do dia a dia abertos; se continuar acima de 85%, 32 GB.", true));

        if (f.SystemWaitDropPct >= 40)
            list.Add(new("Quedas por espera do sistema",
                $"em {N0(f.SystemWaitDropPct!.Value)}% das quedas nem a CPU nem a GPU estavam trabalhando: o jogo esperou o Windows.",
                "rodar uma captura com o diagnóstico profundo (DPC/ISR) para achar o driver, e conferir paginação e programas em segundo plano.", false));

        if (f.PagingAtDropsPct >= 50)
            list.Add(new("Paginação nas quedas",
                $"{N0(f.PagingAtDropsPct!.Value)}% das quedas coincidem com o Windows lendo memória do disco (pico de {N0(f.PagingPeak ?? 0)} páginas/s).",
                "fechar navegador e programas pesados (e a aceleração de hardware do Discord) e medir de novo; se continuar no perfil de uso real, 32 GB.", true));

        if (cpuLimit && f.RamMts is int mts && mts <= 3200 && f.Ryzen5000)
            list.Add(new("Latência da RAM", $"a memória roda a {mts} MT/s num Ryzen 5000 e a CPU é o limite.",
                "testar 3600 MT/s com timings ajustados, validado antes num teste de memória.", false));

        if (f.DropRegularity is double reg && reg < PeriodicBelow)
        {
            bool oldBios = f.BiosAgeMonths is null or >= 12;
            list.Add(new("Quedas em intervalo regular",
                $"as quedas se repetem num ritmo quase constante (variação {reg:0.00} nos intervalos)" +
                (f.BiosDate != null ? $"; BIOS de {f.BiosDate}." : "."),
                oldBios ? "atualizar a BIOS (AGESA mais recente): versões antigas travam periodicamente com o fTPM ligado."
                        : "a BIOS é recente; rodar uma captura com o diagnóstico profundo para achar o driver que atrasa o sistema.", false));
        }

        if (!f.FpsLimited && f.AvgFps > UnlimitedFpsFactor * f.MonitorHz && f.FrametimeStdDevMs > UnlimitedStdDevMs)
            list.Add(new("Fps sem limite",
                $"fps médio {N0(f.AvgFps)} para um monitor de {f.MonitorHz} Hz, com desvio de {f.FrametimeStdDevMs:0.0} ms no frametime.",
                "comparar, em perfis separados, fps_max 240 contra G-Sync + V-Sync + Reflex; ficar com o de menor desvio e julgar a latência jogando.", false));

        if (f.ClockDropPctUnderLoad > ClockDropAbove && f.CpuTempMax < ClockDropTempBelow)
            list.Add(new("Clock cai sob carga sem calor",
                $"o clock médio com a CPU carregada ficou {N0(f.ClockDropPctUnderLoad!.Value)}% abaixo do pico, com no máximo {N0(f.CpuTempMax!.Value)} °C.",
                "pode ser o Curve Optimizer agressivo (clock stretching): testar -15 e boost +100 MHz.", false));

        if (f.SecondMonitorOtherHz && (f.IndependentFlipPct is null || f.IndependentFlipPct < FlipBelow))
            list.Add(new("Segundo monitor em outra taxa",
                f.IndependentFlipPct is double flip ? $"o jogo ficou em Independent Flip em só {N0(flip)}% do tempo, com dois monitores em taxas diferentes."
                                                    : "há dois monitores em taxas diferentes e não foi possível ver o modo de apresentação.",
                "medir com o segundo monitor desligado e sem nada animado nele; comparar também tela cheia exclusiva contra janela sem borda.", false));

        if (!string.IsNullOrEmpty(f.BackgroundCulprit))
            list.Add(new("Programa em segundo plano", $"\"{f.BackgroundCulprit}\" era o mais pesado perto dos engasgos.",
                "fechar ou tirar da inicialização e medir de novo.", false));

        return list;
    }

    /// <summary>Monta os fatos a partir dos arquivos da sessão. <c>null</c> se não há resumo.</summary>
    public static DiagFacts? FromSession(string folder)
    {
        string resumoPath = Path.Combine(folder, "resumo.json");
        if (!File.Exists(resumoPath)) return null;
        var r = JsonNode.Parse(File.ReadAllText(resumoPath))?.AsObject();
        if (r == null) return null;
        var ctx = SessionContext.Load(folder);
        double? D(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

        double avg = D(r["fps_medio"]) ?? 0, low1 = D(r["low_1pct_fps"]) ?? 0;
        int hz = ctx?.Monitor?.HzAtual ?? 0;
        if (avg <= 0 || hz <= 0) return null;

        var sens = CsvTable.Read(Path.Combine(folder, "sensores.csv"),
            c => c is "t_s" or "fps" || Regex.IsMatch(c, @"\| (GPU Core|Memory|CPU Core Max|CPU Core #\d+( Thread #\d+)?|Core #\d+.*) \((%|MHz)\)$|Memory \(%\)$"));
        double? gpuLow = null, threadLow = null, ramMax = null, clockDrop = null;
        if (sens != null && sens.Rows.Count >= 20)
        {
            string? gpuCol = sens.Index.Keys.FirstOrDefault(k => k.EndsWith("| GPU Core (%)", StringComparison.OrdinalIgnoreCase));
            string? ramCol = sens.Index.Keys.FirstOrDefault(k => Regex.IsMatch(k, @"\| Memory \(%\)$") && !k.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
            var threadCols = sens.Index.Keys.Where(k => Regex.IsMatch(k, @"CPU Core #\d+( Thread #\d+)? \(%\)$")).ToList();
            var clockCols = sens.Index.Keys.Where(k => Regex.IsMatch(k, @"Core #\d+.*\(MHz\)$") && !k.Contains("Bus", StringComparison.OrdinalIgnoreCase)).ToList();
            // O clock "Effective" do LibreHardwareMonitor conta o tempo em que o núcleo dormiu ou esticou o clock (clock stretching): é o que importa aqui.
            var effective = clockCols.Where(k => k.Contains("Effective", StringComparison.OrdinalIgnoreCase)).ToList();
            if (effective.Count > 0) clockCols = effective;
            var fpsPerRow = sens.Rows.Select(row => sens.Num(row, "fps") ?? 0).ToList();
            double lowCut = fpsPerRow.Where(x => x > 0).OrderBy(x => x).ElementAtOrDefault(Math.Max(0, (int)(fpsPerRow.Count * 0.10)));
            var lowRows = sens.Rows.Where((row, i) => fpsPerRow[i] > 0 && fpsPerRow[i] <= lowCut).ToList();
            if (lowRows.Count >= 3)
            {
                if (gpuCol != null) gpuLow = lowRows.Average(row => sens.Num(row, gpuCol) ?? 0);
                if (threadCols.Count > 0) threadLow = lowRows.Average(row => threadCols.Max(c => sens.Num(row, c) ?? 0));
            }
            if (ramCol != null) ramMax = sens.Rows.Max(row => sens.Num(row, ramCol) ?? 0);
            if (clockCols.Count > 0 && threadCols.Count > 0)
            {
                var loaded = sens.Rows.Where(row => threadCols.Max(c => sens.Num(row, c) ?? 0) > 80).Select(row => clockCols.Average(c => sens.Num(row, c) ?? 0)).Where(x => x > 0).ToList();
                var all = sens.Rows.Select(row => clockCols.Max(c => sens.Num(row, c) ?? 0)).Where(x => x > 0).OrderBy(x => x).ToList();
                if (loaded.Count >= 5 && all.Count > 0)
                {
                    double peak = all[(int)(all.Count * 0.95) >= all.Count ? all.Count - 1 : (int)(all.Count * 0.95)];
                    clockDrop = peak > 0 ? 100 * (peak - loaded.Average()) / peak : null;
                }
            }
        }

        // Paginação: % das quedas (em segundos) com páginas lidas do disco acima de 3x a mediana da sessão (mínimo 50/s)
        double? pagingPct = null, pagingPeak = null;
        var pg = CsvTable.Read(Path.Combine(folder, "sensores.csv"), c => c is "t_s" or "paginas_entrada_s");
        if (pg != null && pg.Index.ContainsKey("paginas_entrada_s"))
        {
            var rows = pg.Rows.Select(rw => (T: pg.Num(rw, "t_s") ?? double.NaN, P: pg.Num(rw, "paginas_entrada_s"))).Where(x => x.P != null && !double.IsNaN(x.T)).ToList();
            var drops = FrameMetrics.Drops(ProfileReport.ReadFrames(folder));
            if (rows.Count >= 10 && drops.Count >= 4)
            {
                var sortedP = rows.Select(x => x.P!.Value).OrderBy(x => x).ToList();
                double thr = Math.Max(50, 3 * sortedP[sortedP.Count / 2]);
                var dropSecs = drops.Select(d => Math.Ceiling(d)).Distinct().ToList();
                int hit = dropSecs.Count(sec => rows.Any(x => Math.Abs(x.T - sec) <= 1 && x.P > thr));
                pagingPct = 100.0 * hit / dropSecs.Count;
                pagingPeak = sortedP[^1];
            }
        }

        double? sysWait = null, cpuDrop = null;
        if (r["exibicao"]?["quedas_causa_pct"] is JsonObject qc)
        {
            sysWait = D(qc["Sistema"]) ?? 0;
            cpuDrop = D(qc["CPU"]) ?? 0;
        }

        double? flip = null;
        if (r["exibicao"]?["modo_apresentacao_pct"] is JsonObject modes && modes.Count > 0)
            flip = modes.Where(kv => kv.Key.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase)).Sum(kv => D(kv.Value) ?? 0);

        int? biosAge = null;
        if (DateTime.TryParse(ctx?.BiosData, CultureInfo.InvariantCulture, DateTimeStyles.None, out var biosDate))
            biosAge = (int)((DateTime.Now - biosDate).TotalDays / 30.4);
        int? ramMts = ctx?.Ram.Count > 0 ? ctx.Ram.Min(m => m.VelocidadeConfiguradaMts) : null;
        var hzs = ctx?.Monitores.Select(m => Regex.Match(m, @"a (\d+) Hz").Groups[1].Value).Where(x => x.Length > 0).Distinct().ToList() ?? new();
        string? culprit = null;
        try { SessionAnalyzer.AttributeStutters(folder, ctx, out culprit); } catch { }

        return new DiagFacts(gpuLow, threadLow, ramMax, ramMts, Regex.IsMatch(ctx?.Cpu ?? "", @"Ryzen \d 5\d{3}"), biosAge, ctx?.BiosData,
            D(r["quedas_regularidade"]), avg, low1, hz, D(r["desvio_padrao_ms"]) ?? 0,
            int.TryParse(ctx?.Cs2FpsMax, out int fm) && fm > 0, clockDrop, D(r["cpu_temp_max_c"]), hzs.Count > 1, flip, culprit,
            pagingPct, pagingPeak, sysWait, cpuDrop);
    }
}
