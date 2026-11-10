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
    private LogTail? _benchTail;
    private DateTime? _benchEnd;
    private int _benchSecs, _benchSeconds, _benchMaxMinutes;
    private Func<SessionContext>? _benchRecollect;
    private bool _captureArmed;
    private int _armedPid;

    private sealed record CsBenchRun(string Profile, bool WarmUp, bool LostFocus, bool AntiCheat, (double Avg, double P1)? VProf,
                                   Func<SessionContext>? Recollect, bool GameClosed);

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
                if (VProfLog.TryParseSummary(line) is { } v) { _benchVProf = v; StopSession("o mapa terminou"); return; }
        if (_benchMaxMinutes > 0 && s.Elapsed > TimeSpan.FromMinutes(_benchMaxMinutes)) StopSession($"limite de {_benchMaxMinutes} min da captura");
    }

    /// <summary>Chamado por StopSession: guarda o que a captura sabe e zera o estado.</summary>
    private CsBenchRun? BenchSnapshotAndReset(string reason)
    {
        if (!_benchActive) return null;
        var run = new CsBenchRun(_benchProfile!, _benchWarmup, _benchLostFocus, _benchAntiCheat, _benchVProf, _benchRecollect,
                               reason.Contains("fechou", StringComparison.OrdinalIgnoreCase));
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

        var preview = s.Preview();
        string? invalid = RunValidator.Validate(new RunFacts(preview.Frames, s.Elapsed.TotalSeconds, bench.LostFocus, ctxChanged, pmLost, bench.AntiCheat));
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
    }

    private void BtnMatrixUse_Click(object sender, RoutedEventArgs e)
    {
        if (ExperimentMatrix.Load().Current is not { } c) return;
        SessProfile.Text = c.Profile;
        if (string.IsNullOrWhiteSpace(TxtBenchRepeat.Text)) TxtBenchRepeat.Text = ProfileComparer.MinRuns.ToString();
        CsBenchStatus.Text = $"Perfil \"{c.Profile}\" pronto. Faça a mudança ({c.Change}), abra o mapa e clique em CAPTURA CS2.";
    }

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
}
