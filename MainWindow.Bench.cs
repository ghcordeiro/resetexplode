using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ResetExplode;

/// <summary>
/// Sessões › Benchmark CS2: captura armada (começa quando o CS2 está em primeiro plano), perfis de teste, validação da execução
/// e comparação por perfil com o ruído medido. Nunca toca o processo do jogo: só ETW, sensores e o console.log que o jogo escreve.
/// </summary>
public partial class MainWindow
{
    private bool _benchAltTab, _benchActive, _benchForce, _benchLostFocus, _benchAntiCheat, _benchWarmup, _benchOverlayWasOn;
    private string? _benchProfile;
    private (double Avg, double P1)? _benchVProf;
    private readonly List<string> _benchVLines = new();
    private DateTime? _benchStopAt;
    private LogTail? _benchTail;
    private DateTime? _benchEnd;
    private int _benchSecs, _benchSeconds, _benchMaxMinutes;
    private Func<SessionContext>? _benchRecollect;
    private bool _captureArmed;
    private int _armedPid;

    private sealed record CsBenchRun(string Profile, bool WarmUp, bool LostFocus, bool AntiCheat, (double Avg, double P1)? VProf,
                                   Func<SessionContext>? Recollect, bool GameClosed, List<string> VLines);

    private static readonly string ManualProfilePath = Path.Combine(AppSettings.Folder, "perfil-manual.json");

    // ------------------------------------------------------------------ captura

    // Captura em série: depois de cada captura salva, rearma sozinho até juntar N execuções válidas do mesmo perfil.
    private string? _seriesProfile;
    private int _seriesTarget, _seriesStartValid, _seriesAttempts;
    private bool _seriesIsNoise;

    private void BtnCapture_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null && _benchActive) { _seriesProfile = null; StopSession("parado pelo usuário"); return; }
        if (_captureArmed) { _seriesProfile = null; DisarmCapture("Captura cancelada."); return; }
        string? profile = SessionProfile.Normalize(SessProfile.Text);
        if (profile == null) { CsBenchStatus.Text = "Dê um nome ao perfil de teste (ex.: linha de base)."; return; }
        int repeat = int.TryParse(TxtBenchRepeat.Text.Trim(), out int r) ? Math.Clamp(r, 1, 20) : 1;
        StartSeries(profile, repeat, noise: false);
    }

    /// <summary>TESTE DE RUÍDO: 5 capturas da mesma configuração, num perfil próprio, para saber se o benchmark é confiável.</summary>
    private void BtnNoiseTest_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null || _captureArmed) { CsBenchStatus.Text = "Termine a captura em curso antes."; return; }
        string profile = $"ruido-{DateTime.Now:yyyyMMdd-HHmm}";
        SessProfile.Text = profile;
        ChkBenchWarmup.IsChecked = false;
        StartSeries(profile, 5, noise: true);
    }

    private void StartSeries(string profile, int target, bool noise)
    {
        _seriesProfile = target > 1 ? profile : null;
        _seriesTarget = target;
        _seriesIsNoise = noise;
        _seriesAttempts = 0;
        _seriesStartValid = SessionProfile.Runs(profile).Count(x => x.Valid && !x.WarmUp);
        string? err = ArmCapture();
        if (err != null) { _seriesProfile = null; CsBenchStatus.Text = err; }
        else if (target > 1) CsBenchStatus.Text += $" Série: {target} capturas válidas; depois de cada uma, rode o mapa de novo.";
    }

    /// <summary>Arma a captura com o perfil da caixa. Devolve o motivo se não deu para armar.</summary>
    private string? ArmCapture()
    {
        if (_session != null) return "Já há uma gravação em curso. Pare-a antes de iniciar a captura.";
        if (SessionProfile.Normalize(SessProfile.Text) == null) return "Dê um nome ao perfil de teste (ex.: linha de base).";
        if (AntiCheatGuard.IsRunning()) return "FACEIT Anti-Cheat em execução. Feche o cliente da FACEIT: o benchmark só roda offline.";
        var cs2 = Process.GetProcessesByName("cs2").FirstOrDefault();
        if (cs2 == null) return "Abra o CS2 e carregue o mapa de benchmark.";

        string exe = "";
        try { exe = cs2.MainModule?.FileName ?? ""; } catch { }
        _armedPid = cs2.Id;
        _benchTail = VProfLog.PathFromExe(exe) is string log ? new LogTail(log) : null;
        _benchSeconds = int.TryParse(TxtBenchSeconds.Text.Trim(), out int n) && n > 0 ? Math.Min(n, 3600) : 0;
        _captureArmed = true;
        BtnCapture.Content = "CANCELAR CAPTURA";
        CsBenchStatus.Text = "Captura armada. Volte ao CS2: ela começa quando o jogo estiver em primeiro plano.";
        // Experimentos ligados (HAGS, timer, MPO, dynamic tick, prioridade...) mudam o resultado: avisa para o perfil não misturar duas mudanças.
        try
        {
            var on = _tweaks.Where(t => t.Id.StartsWith("exp.") || t.Id == "hags").Where(t => { try { return t.Read() == true; } catch { return false; } }).Select(t => t.Title).ToList();
            if (on.Count > 0) CsBenchStatus.Text += $" ATENÇÃO: experimentos ligados agora: {string.Join("; ", on)}. Se não fazem parte deste perfil, desligue-os em Otimização e reinicie se pedir.";
        }
        catch { }
        return null;
    }

    /// <summary>Chamado quando uma captura do benchmark terminou de salvar (thread da tela): continua a série, se houver.</summary>
    private void BenchAfterSave(string profile)
    {
        try
        {
            if (ExperimentMatrix.Load().Evaluate(profile) is string m) { CsBenchStatus.Text += " " + m; RefreshMatrix(); UpdateBaselineWarning(); }
        }
        catch (Exception ex) { ErrorReporter.Log("matriz", ex); }
        if (_seriesProfile == null || !string.Equals(_seriesProfile, profile, StringComparison.OrdinalIgnoreCase)) return;
        var runs = SessionProfile.Runs(profile);
        int done = runs.Count(x => x.Valid && !x.WarmUp) - _seriesStartValid;
        _seriesAttempts++;
        if (done >= _seriesTarget || _seriesAttempts >= _seriesTarget + 3)
        {
            var noise = ProfileComparer.Noise(runs);
            string verdict = noise.Runs < ProfileComparer.MinRuns ? "poucas execuções válidas para medir o ruído"
                : $"variação do 1% low de {noise.RangePct:0.#}% (desvio {noise.CvPct:0.#}%): " +
                  (noise.Reliable ? "o benchmark é confiável para comparar." : "acima de 5%: aumente a duração, reinicie o PC antes e mantenha as condições iguais.");
            CsBenchStatus.Text = $"Série \"{profile}\" terminou: {done} de {_seriesTarget} válidas. " + verdict;
            if (_seriesIsNoise && noise.Reliable)
            {
                var mx = ExperimentMatrix.Load();
                if (mx.BaselineProfile == null && mx.AdoptBaseline(profile)) CsBenchStatus.Text += " Ele virou a linha de base da matriz: pode ir direto ao próximo passo.";
                RefreshMatrix();
            }
            _seriesProfile = null;
            if (_seriesIsNoise) ErrorReporter.Log($"teste de ruído: {verdict}");
            return;
        }
        string? err = ArmCapture();
        CsBenchStatus.Text = err ?? $"Série \"{profile}\": {done} de {_seriesTarget} válidas. Rode o mapa de novo (a captura já está armada).";
        if (err != null) _seriesProfile = null;
    }

    private void DisarmCapture(string message)
    {
        _captureArmed = false;
        _benchTail = null;
        BtnCapture.Content = "CAPTURA CS2";
        CsBenchStatus.Text = message;
    }

    /// <summary>Timer de 1 s (sem sessão em curso): espera o CS2 vir para a frente e então começa a gravar.</summary>
    private bool BenchArmedTick()
    {
        if (!_captureArmed) return false;
        try { using var p = Process.GetProcessById(_armedPid); if (p.HasExited) throw new InvalidOperationException(); }
        catch { DisarmCapture("O CS2 fechou. Captura cancelada."); return true; }
        if (AntiCheatGuard.IsRunning()) { DisarmCapture("FACEIT Anti-Cheat apareceu. Captura cancelada."); return true; }
        if (ForegroundPid() != _armedPid) return true;

        _captureArmed = false;
        _benchForce = true;
        StartSession(_armedPid);
        _benchForce = false; // se a gravação não começou, o próximo jogo detectado não herda o modo benchmark
        BtnCapture.Content = _session != null ? "PARAR CAPTURA" : "CAPTURA CS2";
        return true;
    }

    /// <summary>Chamado por StartSession logo depois de a gravação começar.</summary>
    private void BenchOnSessionStart(GameSession s, string exe)
    {
        string? profile = SessionProfile.Normalize(SessProfile.Text);
        bool isCs2 = Path.GetFileName(exe).Equals("cs2.exe", StringComparison.OrdinalIgnoreCase);
        bool active = profile != null && (_benchForce || isCs2);
        bool manual = _benchForce;
        _benchForce = false;
        _benchActive = active;
        _benchRecollect = null;
        if (!active) { _benchTail = null; return; }

        _benchProfile = profile;
        _benchWarmup = ChkBenchWarmup.IsChecked == true;
        _benchAltTab = ChkBenchAltTab.IsChecked == true;
        _benchLostFocus = false;
        _benchVProf = null;
        _benchVLines.Clear(); _benchStopAt = null;
        _benchSecs = 0;
        _benchAntiCheat = AntiCheatGuard.IsRunning();
        _benchEnd = manual && _benchSeconds > 0 ? DateTime.Now.AddSeconds(_benchSeconds) : null;
        _benchMaxMinutes = manual ? 10 : 0;
        if (_benchTail == null && VProfLog.PathFromExe(exe) is string log) _benchTail = new LogTail(log);

        SessionProfile.Write(s.Folder, profile!);
        try { if (File.Exists(ManualProfilePath)) File.Copy(ManualProfilePath, Path.Combine(s.Folder, "perfil-manual.json"), true); } catch { }

        // O overlay pesa na medição: fica desligado durante a captura.
        _benchOverlayWasOn = ChkOverlay.IsChecked == true;
        if (_benchOverlayWasOn) ChkOverlay.IsChecked = false;
        CsBenchStatus.Text = $"Gravando o perfil \"{profile}\"" + (_benchEnd != null ? $" por {_benchSeconds} s." : ". Termina quando o mapa imprimir o resumo (ou em 10 min).");
    }

    /// <summary>Chamado a cada segundo de uma sessão em curso, depois do registro do segundo.</summary>
    private void BenchTick(GameSession s)
    {
        if (!_benchActive) return;
        _benchSecs++;
        if (_benchSecs > 1 && !_benchAltTab && ForegroundPid() != s.Pid) _benchLostFocus = true;
        if (_benchSecs % 5 == 0 && AntiCheatGuard.IsRunning()) _benchAntiCheat = true;

        if (_benchEnd is DateTime end && DateTime.Now >= end) { StopSession("captura terminada"); return; }
        if (_benchTail != null)
            foreach (var line in _benchTail.ReadNew())
            {
                if (line.Contains("VProf", StringComparison.OrdinalIgnoreCase) && _benchVLines.Count < 300) _benchVLines.Add(line);
                if (VProfLog.TryParseSummary(line) is { } v && _benchVProf == null) { _benchVProf = v; _benchStopAt = DateTime.Now.AddSeconds(4); } // a tabela vem logo depois da linha FPS
                if (_benchVProf != null && line.Contains("VProfLite stopped", StringComparison.OrdinalIgnoreCase)) _benchStopAt = DateTime.Now;
            }
        if (_benchStopAt is DateTime stop && DateTime.Now >= stop) { StopSession("o mapa terminou"); return; }
        if (_benchMaxMinutes > 0 && s.Elapsed > TimeSpan.FromMinutes(_benchMaxMinutes)) StopSession($"limite de {_benchMaxMinutes} min da captura");
    }

    /// <summary>Chamado por StopSession: guarda o que a captura sabe e zera o estado.</summary>
    private CsBenchRun? BenchSnapshotAndReset(string reason)
    {
        if (!_benchActive) return null;
        var run = new CsBenchRun(_benchProfile!, _benchWarmup, _benchLostFocus, _benchAntiCheat, _benchVProf, _benchRecollect,
                               reason.Contains("fechou", StringComparison.OrdinalIgnoreCase), _benchVLines.ToList());
        _benchVLines.Clear(); _benchStopAt = null;
        _benchActive = false; _benchTail = null; _benchRecollect = null; _benchEnd = null;
        if (_benchOverlayWasOn) { ChkOverlay.IsChecked = true; _benchOverlayWasOn = false; }
        BtnCapture.Content = "CAPTURA CS2";
        CsBenchStatus.Text = "Captura terminada. Salvando…";
        return run;
    }

    /// <summary>Thread de salvar: valida a execução e põe perfil, validade e P1 do mapa no resumo.json.</summary>
    private static void BenchApply(CsBenchRun bench, GameSession s, SessionContext? ctx, Dictionary<string, object?> extra)
    {
        extra["perfil"] = bench.Profile;
        extra["aquecimento"] = bench.WarmUp;

        bool ctxChanged = false;
        if (bench.Recollect != null && ctx != null && !bench.GameClosed)
            try { ctxChanged = Fingerprint(bench.Recollect()) != Fingerprint(ctx); } catch { }
        bool pmLost = extra.TryGetValue("exibicao", out var ex) && ex?.GetType().GetProperty("erro") != null;

        // O mapa analisa uma janela (N frames; o resto é aquecimento). Alinha a captura a ela: os últimos N frames, para o 1% low bater com o P1 do mapa.
        var report = VProfLog.ParseReport(bench.VLines);
        bool covered = true;
        if (report != null && report.Frames > 0)
        {
            extra["frames_captura_total"] = s.FrameCount;
            covered = s.TrimToWindow(report.Frames, report.EndTime);
            extra["janela_quadros"] = report.Frames;
            extra["vprof"] = new
            {
                frames = report.Frames, intervalos = report.Intervals, excluidos = report.Excluded, avg = report.Avg, p1 = report.P1,
                linhas = report.Rows.Select(r => new { nome = r.Name, todos_avg_ms = r.AllAvg, todos_p99_ms = r.AllP99, ativos_n = r.ActiveN,
                                                       ativos_avg_ms = r.ActiveAvg, ativos_p99_ms = r.ActiveP99, max1s_p50_ms = r.MaxP50, max1s_p95_ms = r.MaxP95 }).ToList(),
            };
        }
        try { if (bench.VLines.Count > 0) File.WriteAllLines(Path.Combine(s.Folder, "vprof.txt"), bench.VLines); } catch { }

        var preview = s.Preview();
        string? invalid = RunValidator.Validate(new RunFacts(preview.Frames, s.Elapsed.TotalSeconds, bench.LostFocus, ctxChanged, pmLost, bench.AntiCheat));
        if (!covered) invalid = (invalid == null ? "" : invalid + "; ") + "a captura começou depois do início da passada do mapa";
        extra["valida"] = invalid == null;
        extra["motivo_invalida"] = invalid;

        if (bench.VProf is { } v)
        {
            extra["avg_mapa"] = v.Avg;
            extra["p1_mapa"] = v.P1;
            double diff = VProfLog.DiffPct(preview.Low1Fps, v.P1);
            extra["p1_mapa_diferenca_pct"] = Math.Round(diff, 1);
            if (Math.Abs(diff) > 5)
            {
                var avisos = extra.GetValueOrDefault("avisos") as List<string> ?? new List<string>();
                avisos.Add($"AVISO: o 1% low calculado ({preview.Low1Fps:0}) difere {diff:+0.#;-0.#}% do P1 impresso pelo mapa ({v.P1:0.#}).");
                extra["avisos"] = avisos;
            }
        }
    }

    /// <summary>Resumo do que pode mudar uma medição: driver, energia, monitor, RAM e ajustes do Windows.</summary>
    private static string Fingerprint(SessionContext c) => string.Join("|", new object?[]
    {
        c.DriverGpu, c.PlanoEnergia, c.Hags, c.ModoJogo, c.VrrWindows, c.Monitor?.Largura, c.Monitor?.Altura, c.Monitor?.HzAtual,
        string.Join(",", c.Ram.Select(r => r.VelocidadeConfiguradaMts)), string.Join(",", c.AjustesLigados.OrderBy(x => x)),
    });

    // ------------------------------------------------------------------ perfil manual

    private void BtnManualProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            if (!File.Exists(ManualProfilePath))
                File.WriteAllText(ManualProfilePath,
                    "{\n  \"pbo\": \"\",\n  \"curve_optimizer\": \"\",\n  \"boost_override_mhz\": \"\",\n  \"ppt_tdc_edc\": \"\",\n" +
                    "  \"ram_timings\": \"\",\n  \"ram_tensoes\": \"\",\n  \"bios_notas\": \"\"\n}\n");
            ShellRunner.OpenUrl(ManualProfilePath);
            CsBenchStatus.Text = "Preencha o que a BIOS tem e o Windows não lê (PBO, Curve Optimizer, timings). Cada gravação copia esse arquivo.";
        }
        catch (Exception ex) { CsBenchStatus.Text = $"Não foi possível abrir o perfil manual: {ex.Message}"; }
    }

    // ------------------------------------------------------------------ comparação por perfil

    private List<Comparison> _lastComparison = new();

    private void RefreshBenchProfiles()
    {
        var names = SessionProfile.AllProfiles();
        string? a = CmbProfA.SelectedItem as string, b = CmbProfB.SelectedItem as string;
        CmbProfA.ItemsSource = names;
        CmbProfB.ItemsSource = names;
        CmbProfA.SelectedItem = a != null && names.Contains(a) ? a : names.Count > 1 ? names[^1] : null;
        CmbProfB.SelectedItem = b != null && names.Contains(b) ? b : names.Count > 0 ? names[0] : null;
        UpdateBaselineWarning();
        RefreshMatrix();
    }

    private void BtnCompareProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfA.SelectedItem is not string a || CmbProfB.SelectedItem is not string b)
        {
            BenchCompareLines.ItemsSource = new[] { "Grave ao menos um perfil e escolha os dois para comparar." };
            return;
        }
        var ra = SessionProfile.Runs(a);
        var rb = SessionProfile.Runs(b);
        string Count(string name, List<RunRecord> r) =>
            $"{name}: {r.Count(x => x.Valid && !x.WarmUp)} execuções válidas, {r.Count(x => !x.Valid)} inválidas, {r.Count(x => x.Valid && x.WarmUp)} de aquecimento";

        string NoiseLine(string name, List<RunRecord> r)
        {
            var nz = ProfileComparer.Noise(r);
            return nz.Runs < ProfileComparer.MinRuns ? $"{name}: ruído ainda não medido (menos de 3 válidas)"
                : $"{name}: variação do 1% low entre execuções {nz.RangePct:0.#}% ({(nz.Reliable ? "confiável" : "alta: o veredito vale menos")})";
        }
        var lines = new List<string> { Count(a, ra), Count(b, rb), NoiseLine(a, ra), NoiseLine(b, rb), "" };
        _lastComparison = ProfileComparer.CompareRuns(ra, rb);
        lines.AddRange(_lastComparison.Select(c => c.Line));
        foreach (var r in ra.Concat(rb).Where(x => !x.Valid))
            lines.Add($"inválida: {Path.GetFileName(r.Folder)} ({r.Invalid})");

        foreach (var (name, runs) in new[] { (a, ra), (b, rb) })
        {
            var rep = ProfileComparer.RepeatedDrops(runs);
            if (rep.Count > 0)
                lines.Add($"{name}: quedas que se repetem no mesmo instante (eventos da cena): " +
                          string.Join("; ", rep.Select(x => $"~{x.T:0.#} s (quadro ~{x.Frame}, pior {x.WorstMs:0.#} ms, em {x.Runs} de {x.Total})")));
        }
        lines.AddRange(DriverLines(a, ra));
        lines.AddRange(DriverLines(b, rb));
        string? fa = ra.LastOrDefault(x => x.Valid)?.Folder, fb = rb.LastOrDefault(x => x.Valid)?.Folder;
        if (fa != null && fb != null)
        {
            var changes = SessionAnalyzer.ConfigChanges(fa, fb);
            lines.Add("");
            lines.Add(changes.Count > 0 ? "O que mudou no PC: " + string.Join("; ", changes) + "." : "Nenhuma mudança de configuração detectada entre as duas últimas execuções.");
        }
        BenchCompareLines.ItemsSource = lines;
    }

    /// <summary>Gera o HTML da comparação atual (as duas listas de perfis) e abre no navegador.</summary>
    private void BtnProfileReport_Click(object sender, RoutedEventArgs e)
    {
        if (CmbProfA.SelectedItem is not string a || CmbProfB.SelectedItem is not string b)
        {
            BenchCompareLines.ItemsSource = new[] { "Escolha os dois perfis para gerar o relatório." };
            return;
        }
        try
        {
            var ra = SessionProfile.Runs(a); var rb = SessionProfile.Runs(b);
            var rows = ProfileComparer.CompareRuns(ra, rb);
            string? fa = ra.LastOrDefault(x => x.Valid)?.Folder, fb = rb.LastOrDefault(x => x.Valid)?.Folder;
            var diff = fa != null && fb != null ? SessionAnalyzer.ConfigChanges(fa, fb) : new List<string>();
            var sug = new List<Suggestion>();
            if (fb != null && DiagnosisRules.FromSession(fb) is { } facts) sug = DiagnosisRules.Evaluate(facts);
            string html = ProfileReport.Build(a, b, rows, fa != null ? ProfileReport.ReadFrames(fa) : new List<float>(),
                                              fb != null ? ProfileReport.ReadFrames(fb) : new List<float>(), diff, sug);
            string dir = Path.Combine(AppSettings.Folder, "relatorios");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{SessionProfile.Normalize(a)}_vs_{SessionProfile.Normalize(b)}_{DateTime.Now:yyyyMMdd_HHmmss}.html");
            File.WriteAllText(path, html, new System.Text.UTF8Encoding(false));
            ShellRunner.OpenUrl(path);
        }
        catch (Exception ex) { CsBenchStatus.Text = $"Não foi possível gerar o relatório: {ex.Message}"; }
    }

    // ------------------------------------------------------------------ vídeo do CS2 (teste)

    private void BtnVideoRead_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = Cs2Files.VideoPath();
            if (path == null) { CsBenchStatus.Text = "Não achei o cs2_video.txt (abra o CS2 ao menos uma vez neste PC)."; return; }
            TxtVideoChanges.Text = string.Join("\n", Cs2VideoConfig.Editable(Cs2Files.ParseVideo(File.ReadAllText(path))));
            CsBenchStatus.Text = "Edite só os valores que quer testar (pode apagar as linhas que não mudam) e clique em APLICAR.";
        }
        catch (Exception ex) { CsBenchStatus.Text = $"Não foi possível ler o vídeo: {ex.Message}"; }
    }

    private void BtnVideoApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = Cs2Files.VideoPath();
            if (path == null) { CsBenchStatus.Text = "Não achei o cs2_video.txt."; return; }
            var changes = Cs2VideoConfig.ParseChanges(TxtVideoChanges.Text);
            if (changes.Count == 0) { CsBenchStatus.Text = "Escreva ao menos uma opção (chave=valor)."; return; }
            Cs2VideoConfig.ApplyToFile(path, changes);
            CsBenchStatus.Text = $"{changes.Count} opção(ões) gravada(s). Abra o CS2 e grave o perfil. RESTAURAR desfaz.";
        }
        catch (Exception ex) { CsBenchStatus.Text = ex.Message; }
    }

    private void BtnVideoRestore_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = Cs2Files.VideoPath();
            if (path == null) { CsBenchStatus.Text = "Não achei o cs2_video.txt."; return; }
            CsBenchStatus.Text = Cs2VideoConfig.Restore(path) ? "Vídeo do CS2 restaurado ao estado de antes dos testes." : "Não há backup para restaurar.";
        }
        catch (Exception ex) { CsBenchStatus.Text = ex.Message; }
    }

    // ------------------------------------------------------------------ vale comprar? e linha de base vencida

    private void BtnPurchase_Click(object sender, RoutedEventArgs e)
    {
        string? baseline = BaselineProfile();
        if (baseline == null) { BenchCompareLines.ItemsSource = new[] { "Grave a linha de base primeiro." }; return; }
        string? real = SessionProfile.AllProfiles().FirstOrDefault(p => p.Contains("uso real", StringComparison.OrdinalIgnoreCase));
        try { BenchCompareLines.ItemsSource = new[] { $"Linha de base: \"{baseline}\"" }.Concat(PurchaseVerdict.Evaluate(baseline, real)).ToList(); }
        catch (Exception ex) { ErrorReporter.Log("vale comprar", ex); BenchCompareLines.ItemsSource = new[] { "Falhou: " + ex.Message }; }
    }

    /// <summary>Linha de base atual: a da matriz guiada, senão a escolhida em "Linha de base".</summary>
    private string? BaselineProfile() => ExperimentMatrix.Load().BaselineProfile is { Length: > 0 } m ? m : CmbProfA.SelectedItem as string;

    /// <summary>Avisa quando o CS2, o driver, o Windows ou a BIOS mudaram depois da última medição da linha de base (o PRD manda medir de novo).</summary>
    private void UpdateBaselineWarning()
    {
        BenchBaselineWarn.Text = "";
        try
        {
            if (BaselineProfile() is not string baseline) return;
            var last = SessionProfile.Runs(baseline).LastOrDefault(r => r.Valid);
            var newest = SessionStore.List().Select(i => SessionContext.Load(i.Folder)).FirstOrDefault(c => c != null);
            if (last == null || newest == null || SessionContext.Load(last.Folder) is not SessionContext b) return;
            var changed = new List<string>();
            void Cmp(string what, string? x, string? y) { if (x != null && y != null && x != y) changed.Add($"{what} ({x} → {y})"); }
            Cmp("CS2", b.Cs2Build, newest.Cs2Build);
            Cmp("driver da GPU", b.DriverGpu, newest.DriverGpu);
            Cmp("Windows", b.Windows, newest.Windows);
            Cmp("BIOS", b.BiosVersao, newest.BiosVersao);
            if (changed.Count > 0)
                BenchBaselineWarn.Text = $"A linha de base \"{baseline}\" foi medida antes de mudar: {string.Join("; ", changed)}. Meça a linha de base de novo antes de comparar.";
        }
        catch (Exception ex) { ErrorReporter.Log("aviso de linha de base", ex); }
    }

    // ------------------------------------------------------------------ matriz guiada

    private void RefreshMatrix()
    {
        var m = ExperimentMatrix.Load();
        MatrixList.ItemsSource = m.Steps.OrderBy(x => x.Order).Select(x => x.Line).ToList();
        MatrixNext.Text = m.Current is { } c
            ? $"Próximo: \"{c.Profile}\" — mude só isto: {c.Change}. (hipótese: {c.Hypothesis})"
            : "Matriz concluída. Use VALE COMPRAR? para a decisão de compra.";
        MatrixBaseline.Text = m.BaselineProfile != null ? $"Linha de base atual: \"{m.BaselineProfile}\"" : "Linha de base ainda não medida.";
        DrawBaselineHistory();
    }

    private void BtnMatrixUse_Click(object sender, RoutedEventArgs e)
    {
        var m = ExperimentMatrix.Load();
        if (m.Current is not { } c) return;
        if (c.Profile.Contains("ram 3600", StringComparison.OrdinalIgnoreCase) &&
            MessageBox.Show(this, "Antes de medir a RAM a 3600:\n\n1. Salve um OC Profile na BIOS.\n2. Aplique 3600 MT/s com os timings.\n" +
                                  "3. Rode o stress de RAM por 30 min (RESET DESEMPENHO › Stress test) sem nenhum erro.\n4. Confira em Overclock que não houve erro WHEA.\n\n" +
                                  "Já fez os 4 passos?", "RAM a 3600", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            NavStress.IsChecked = true;
            return;
        }
        if (c.Video is { Count: > 0 })
        {
            try
            {
                string path = Cs2Files.VideoPath() ?? throw new InvalidOperationException("não achei o cs2_video.txt");
                var now = Cs2Files.ParseVideo(File.ReadAllText(path));
                c.VideoBefore = c.Video.Keys.Where(now.ContainsKey).ToDictionary(k => k, k => now[k]);
                Cs2VideoConfig.ApplyToFile(path, c.Video);
                m.Save();
            }
            catch (Exception ex) { CsBenchStatus.Text = "Não gravei o vídeo: " + ex.Message; return; }
        }
        SessProfile.Text = c.Profile;
        if (string.IsNullOrWhiteSpace(TxtBenchRepeat.Text)) TxtBenchRepeat.Text = ProfileComparer.MinRuns.ToString();
        CsBenchStatus.Text = $"Perfil \"{c.Profile}\" pronto. Faça a mudança ({c.Change}), abra o mapa e clique em CAPTURA CS2.";
    }

    private void BtnMatrixAdopt_Click(object sender, RoutedEventArgs e)
    {
        var m = ExperimentMatrix.Load();
        bool Enough(string p) => SessionProfile.Runs(p).Count(r => r.Valid && !r.WarmUp) >= ProfileComparer.MinRuns;
        string? typed = SessionProfile.Normalize(SessProfile.Text);
        string? cand = typed != null && Enough(typed) ? typed : SessionProfile.AllProfiles().FirstOrDefault(Enough);
        if (cand == null) { CsBenchStatus.Text = "Nenhum teste com 3 execuções válidas ainda."; return; }
        m.AdoptBaseline(cand);
        RefreshMatrix();
        CsBenchStatus.Text = $"\"{cand}\" virou a linha de base. Clique em USAR O PRÓXIMO PASSO.";
    }

    private void ChkMatrixAll_Click(object sender, RoutedEventArgs e) => MatrixList.Visibility = ChkMatrixAll.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void BtnMatrixSkip_Click(object sender, RoutedEventArgs e)
    {
        var m = ExperimentMatrix.Load();
        if (m.Current is not { } c) return;
        c.Status = "pulado";
        m.Save();
        RefreshMatrix();
    }

    private void BtnMatrixReset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Recomeçar a matriz do zero? As sessões gravadas continuam; só o andamento dos passos volta ao início.", "Matriz",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ExperimentMatrix.Default().Save();
        RefreshMatrix();
    }

    private void BtnMatrixRevert_Click(object sender, RoutedEventArgs e)
    {
        var m = ExperimentMatrix.Load();
        var step = m.Steps.Where(x => x.VideoBefore is { Count: > 0 } && x.Status is "perdeu" or "ruído" or "medindo").OrderByDescending(x => x.Order).FirstOrDefault();
        if (step == null) { CsBenchStatus.Text = "Nada a reverter: os passos que não são de vídeo você desfaz à mão (o texto do passo diz o quê)."; return; }
        try
        {
            string path = Cs2Files.VideoPath() ?? throw new InvalidOperationException("não achei o cs2_video.txt");
            Cs2VideoConfig.ApplyToFile(path, step.VideoBefore!);
            step.VideoBefore = null;
            m.Save();
            CsBenchStatus.Text = $"Vídeo do passo \"{step.Profile}\" revertido.";
        }
        catch (Exception ex) { CsBenchStatus.Text = "Não reverti: " + ex.Message; }
    }

    private void BtnMatrixVideo_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = Cs2Files.VideoPath() ?? throw new InvalidOperationException("não achei o cs2_video.txt (abra o CS2 uma vez)");
            var m = ExperimentMatrix.Load();
            int n = m.AddVideoSteps(Cs2Files.ParseVideo(File.ReadAllText(path)));
            m.Save();
            RefreshMatrix();
            CsBenchStatus.Text = n > 0 ? $"{n} passo(s) de vídeo entraram na matriz, antes das compras." : "Nenhuma opção de vídeo pesada para testar (ou já estão na matriz).";
        }
        catch (Exception ex) { CsBenchStatus.Text = ex.Message; }
    }

    // ------------------------------------------------------------------ dados: driver, exportar, histórico

    /// <summary>Linhas "por versão do driver" de um perfil (só quando há mais de uma versão).</summary>
    private static List<string> DriverLines(string profile, List<RunRecord> runs)
    {
        var groups = runs.Where(r => r.Valid && !r.WarmUp)
            .GroupBy(r => SessionContext.Load(r.Folder)?.DriverGpu ?? "?").ToList();
        if (groups.Count < 2) return new();
        var lines = new List<string> { $"{profile} por versão do driver da GPU:" };
        foreach (var g in groups)
        {
            var m = ProfileComparer.Summarize("1% low", g.Select(r => r.Low1Fps));
            lines.Add($"  driver {g.Key}: 1% low {m.Median:0} [{m.Min:0}–{m.Max:0}] em {m.Runs} execução(ões)");
        }
        lines.Add("  Execuções com drivers diferentes no mesmo perfil misturam duas configurações: meça de novo com um só.");
        return lines;
    }

    private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var profiles = new[] { CmbProfA.SelectedItem as string, CmbProfB.SelectedItem as string }.OfType<string>().Distinct().ToList();
        if (profiles.Count == 0) { CsBenchStatus.Text = "Escolha os perfis."; return; }
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder("perfil,sessao,valida,aquecimento,low_1pct,low_01pct,fps_medio,desvio_ms,quedas,alttab_ms,lat_tela_ms,lat_pc_ms,clique_ms,bios,driver,cs2_build,fps_max,motivo\n");
            foreach (var p in profiles)
                foreach (var r in SessionProfile.Runs(p))
                {
                    var c = SessionContext.Load(r.Folder);
                    string Q(string? x) => "\"" + (x ?? "").Replace("\"", "'") + "\"";
                    sb.Append(string.Join(',', new[]
                    {
                        Q(p), Q(Path.GetFileName(r.Folder)), r.Valid ? "1" : "0", r.WarmUp ? "1" : "0",
                        r.Low1Fps.ToString("0.#", inv), r.Low01Fps.ToString("0.#", inv), r.AvgFps.ToString("0.#", inv), r.StdDevMs.ToString("0.###", inv),
                        r.Drops.ToString(inv), r.AltTabMs.ToString("0", inv), r.DisplayLatMs.ToString("0.##", inv), r.PcLatMs.ToString("0.##", inv), r.ClickLatMs.ToString("0.##", inv),
                        Q(c?.BiosVersao), Q(c?.DriverGpu), Q(c?.Cs2Build), Q(c?.Cs2FpsMax), Q(r.Invalid),
                    })).Append('\n');
                }
            string dir = Path.Combine(AppSettings.Folder, "relatorios");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{string.Join("_vs_", profiles)}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
            ShellRunner.OpenUrl(dir);
            CsBenchStatus.Text = "CSV salvo em " + path;
        }
        catch (Exception ex) { CsBenchStatus.Text = "Não exportei: " + ex.Message; }
    }

    /// <summary>1% low de cada execução válida das linhas de base (a inicial e cada vencedora) ao longo do tempo.</summary>
    private void DrawBaselineHistory()
    {
        BaselineChart.Children.Clear();
        try
        {
            var m = ExperimentMatrix.Load();
            var profiles = m.Steps.Where(x => x.Status is "base" or "venceu").Select(x => x.Profile).ToList();
            var pts = profiles.SelectMany(p => SessionProfile.Runs(p).Where(r => r.Valid && !r.WarmUp)
                              .Select(r => (When: Directory.GetCreationTime(r.Folder), r.Low1Fps)))
                              .OrderBy(x => x.When).ToList();
            BaselineHistoryLabel.Text = pts.Count < 2 ? "Histórico da linha de base: aparece com 2 ou mais execuções." :
                $"Histórico da linha de base: 1% low de {pts.Min(x => x.Low1Fps):0} a {pts.Max(x => x.Low1Fps):0} fps em {pts.Count} execuções.";
            if (pts.Count < 2) return;
            double w = Math.Max(200, BaselineChart.ActualWidth), h = BaselineChart.Height;
            double lo = pts.Min(x => x.Low1Fps) * 0.95, hi = pts.Max(x => x.Low1Fps) * 1.05;
            var line = new System.Windows.Shapes.Polyline { StrokeThickness = 1.5 };
            line.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Fg");
            for (int i = 0; i < pts.Count; i++)
            {
                double x = w * i / (pts.Count - 1), y = h - h * (pts[i].Low1Fps - lo) / Math.Max(1, hi - lo);
                line.Points.Add(new Point(x, y));
            }
            BaselineChart.Children.Add(line);
        }
        catch (Exception ex) { ErrorReporter.Log("histórico", ex); }
    }

    private void BtnMatrixCo_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Descoberta do Curve Optimizer:\n\n• Desligue a Integridade de Memória (Segurança do Windows › Isolamento de núcleo) e reinicie.\n" +
                                  "• Não abra a FACEIT nessa fase (o app avisa se ela abrir com a Integridade desligada).\n" +
                                  "• Aplique cada valor pelo PBO2 Tuner ou Ryzen Master, sem reiniciar entre eles, e meça.\n" +
                                  "• Os números dessa fase só valem entre si. No fim: grave o vencedor na BIOS, religue a Integridade, reinicie e meça a linha de base de novo.\n\n" +
                                  "Acrescentar os passos -20, -15 e -10 à matriz?", "Descoberta do CO", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var m = ExperimentMatrix.Load();
        int n = m.AddCurveDiscovery();
        m.Save();
        RefreshMatrix();
        CsBenchStatus.Text = n > 0 ? $"{n} passo(s) de descoberta na matriz." : "Os passos de descoberta já estão na matriz.";
    }

    // ------------------------------------------------------------------ modo simples

    private void ChkAdvanced_Click(object sender, RoutedEventArgs e) =>
        BenchAdvanced.Visibility = ChkAdvanced.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Resumo do teste em Markdown: o que mudou, os números de cada execução, o laudo da última e os eventos de queda.</summary>
    private string BuildAnalysisReport(string profile)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var runs = SessionProfile.Runs(profile);
        var sb = new System.Text.StringBuilder();
        sb.Append($"**Teste:** {profile} · **App:** {Updater.CurrentVersion} · {DateTime.Now:yyyy-MM-dd HH:mm}\n\n");

        sb.Append("### Execuções\n\n| # | válida | aquec. | 1% low | 0,1% low | fps médio | desvio ms | quedas | lat. tela ms | motivo |\n|---|---|---|---|---|---|---|---|---|---|\n");
        for (int i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            sb.Append($"| {i + 1} | {(r.Valid ? "sim" : "NÃO")} | {(r.WarmUp ? "sim" : "")} | {r.Low1Fps.ToString("0", inv)} | {r.Low01Fps.ToString("0", inv)} | {r.AvgFps.ToString("0", inv)} | {r.StdDevMs.ToString("0.00", inv)} | {r.Drops} | {r.DisplayLatMs.ToString("0.#", inv)} | {r.Invalid} |\n");
        }
        var nz = ProfileComparer.Noise(runs);
        sb.Append(nz.Runs >= ProfileComparer.MinRuns
            ? $"\nVariação do 1% low entre execuções: {nz.RangePct.ToString("0.#", inv)}% ({(nz.Reliable ? "confiável" : "alta")}).\n"
            : "\nMenos de 3 execuções válidas: ruído não medido.\n");

        // Sensores de cada execução válida (média; máximo entre parênteses): temperatura, watts, tensão, clock e carga
        try
        {
            var valid = runs.Where(r => r.Valid && !r.WarmUp).TakeLast(5).ToList();
            var stats = valid.Select(r => SensorSummary.Compute(r.Folder)).ToList();
            var labels = stats.SelectMany(x => x.Select(y => y.Label)).Distinct().ToList();
            if (labels.Count > 0)
            {
                sb.Append("\n### Sensores durante a captura (média, máx entre parênteses)\n\n| métrica |" + string.Join("|", valid.Select((_, i) => $" #{i + 1} ")) + "|\n|---|" + string.Join("|", valid.Select(_ => "---")) + "|\n");
                foreach (var l in labels)
                    sb.Append($"| {l} |" + string.Join("|", stats.Select(x => x.FirstOrDefault(y => y.Label == l) is { } y ? $" {y.Avg.ToString("0.#", inv)} ({y.Max.ToString("0.#", inv)}) " : " — ")) + "|\n");
            }
        }
        catch (Exception ex) { ErrorReporter.Log("sensores no resumo", ex); }

        var m = ExperimentMatrix.Load();
        if (m.BaselineProfile != null && !m.BaselineProfile.Equals(profile, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append($"\n### Contra a linha de base \"{m.BaselineProfile}\"\n\n");
            foreach (var c in ProfileComparer.CompareRuns(SessionProfile.Runs(m.BaselineProfile), runs)) sb.Append("- ").Append(c.Line).Append('\n');
        }
        var rep = ProfileComparer.RepeatedDrops(runs);
        if (rep.Count > 0)
            sb.Append("\nQuedas que se repetem no mesmo instante: " + string.Join("; ", rep.Select(x => $"~{x.T:0.#} s (quadro ~{x.Frame}, pior {x.WorstMs:0.#} ms, em {x.Runs} de {x.Total})")) + "\n");
        if (m.Current is { } cur) sb.Append($"\nPasso atual da matriz: {cur.Profile} — {cur.Change}\n");

        var last = runs.LastOrDefault(r => r.Valid) ?? runs.LastOrDefault();
        if (last != null)
        {
            string laudo = System.IO.Path.Combine(last.Folder, "laudo.txt"), vprof = System.IO.Path.Combine(last.Folder, "vprof.txt");
            if (File.Exists(laudo)) sb.Append("\n<details><summary>Laudo da última execução</summary>\n\n```\n").Append(File.ReadAllText(laudo)).Append("\n```\n</details>\n");
            if (File.Exists(vprof)) sb.Append("\n<details><summary>Tabela do mapa (VProf)</summary>\n\n```\n").Append(File.ReadAllText(vprof)).Append("\n```\n</details>\n");
            if (SessionContext.Load(last.Folder) is { } c)
            {
                sb.Append("\n<details><summary>Configuração do PC nessa execução</summary>\n\n```\n");
                sb.Append($"BIOS {c.BiosVersao} ({c.BiosData}) · driver GPU {c.DriverGpu} · CS2 build {c.Cs2Build} · fps_max {c.Cs2FpsMax} · opções {c.Cs2OpcoesInicio}\n");
                sb.Append($"Windows {c.Windows} · energia {c.PlanoEnergia} · HAGS {c.Hags} · VBS {c.Vbs} · SecureBoot {c.SecureBoot} · TPM {c.Tpm}\n");
                sb.Append("Monitores: " + string.Join(" + ", c.Monitores) + "\nRAM: " + string.Join(", ", c.Ram.Select(r => $"{r.CapacidadeGb:0}GB {r.VelocidadeConfiguradaMts}MT/s")) + "\n");
                sb.Append("Perfil NVIDIA: " + c.PerfilNvidia + "\nAjustes ligados: " + string.Join("; ", c.AjustesLigados) + "\nProgramas abertos: " + string.Join(", ", c.ProgramasAbertos) + "\n");
                sb.Append("\n```\n</details>\n");
            }
        }
        return sb.ToString();
    }

    private async void BtnSendAnalysis_Click(object sender, RoutedEventArgs e)
    {
        string? profile = SessionProfile.Normalize(SessProfile.Text) ?? SessionProfile.AllProfiles().FirstOrDefault();
        if (profile == null) { CsBenchStatus.Text = "Ainda não há nenhum teste gravado. Faça uma captura primeiro."; return; }
        if (string.IsNullOrEmpty(_settings.GitHubToken))
        {
            CsBenchStatus.Text = "Falta o token do GitHub (só na primeira vez): cole na janela que abriu.";
            string? lastSession = null;
            try { lastSession = SessionStore.List().FirstOrDefault()?.Folder; } catch { }
            new ProblemReportWindow(_settings, lastSession) { Owner = this }.ShowDialog();
            if (string.IsNullOrEmpty(_settings.GitHubToken)) return;
        }
        CsBenchStatus.Text = "Enviando…";
        try
        {
            string result = await ErrorReporter.SendTextAsync($"{profile} · {SessionProfile.Runs(profile).Count} execuções", BuildAnalysisReport(profile));
            CsBenchStatus.Text = result.StartsWith("http") ? $"Enviado. Pode me avisar no chat: {result}" : $"Não enviou: {result}";
        }
        catch (Exception ex) { CsBenchStatus.Text = "Falhou: " + ex.Message; ErrorReporter.Log("enviar análise", ex); }
    }
}
