using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>
/// RESET JOGOS · Sessões: detecta o jogo (programa em primeiro plano, em tela cheia ou janela sem borda, entregando frames)
/// e grava tudo até ele fechar. A gravação roda no timer de 1 s da janela, então funciona com o app minimizado, mas não fechado.
/// </summary>
public partial class MainWindow
{
    private GameSession? _session;
    private List<SensorRow> _sessionRows = new();
    private Process? _sessionProc;
    private List<SessionProbe> _probes = new();
    private PcieProbe? _pcieProbe;
    private Task<SessionContext>? _sessionCtx;
    private int _candidatePid, _candidateSecs, _lastFramePid;
    private static readonly TimeSpan MaxSession = TimeSpan.FromHours(6);

    // Programas que entregam frames mas não são jogos (vídeo, navegador, chat, gravação).
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "applicationframehost", "chrome", "msedge", "msedgewebview2", "firefox", "opera", "opera_gx", "brave", "vivaldi",
        "discord", "obs64", "obs32", "steamwebhelper", "steam", "epicgameslauncher", "eadesktop", "battle.net", "upc", "galaxyclient",
        "vlc", "mpc-hc64", "potplayermini64", "spotify", "teams", "ms-teams", "code", "devenv", "powerpnt", "wmplayer", "video.ui",
        "microsoft.photos", "photos", "netflix", "whatsapp", "telegram", "zoom", "windowsterminal", "icue", "nzxt cam",
    };

    private void InitSessions()
    {
        ChkAutoRecord.IsChecked = _settings.AutoRecordGames;
        ChkSessKeyboard.IsChecked = _settings.SessionKeyboard;
        ShowLastInputTest();
        RefreshSessionList();
        RefreshBenchProfiles();
        UpdateSessionStatus();
    }

    private void ChkAutoRecord_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoRecordGames = ChkAutoRecord.IsChecked == true;
        try { _settings.Save(); } catch { }
        _candidatePid = 0;
        UpdateSessionStatus();
    }

    private void BtnSessToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null) { StopSession("parado pelo usuário"); return; }
        // Manual: grava o último programa que entregou frames em primeiro plano (o usuário clicou aqui, então o RESET EXPLODE está na frente).
        if (_lastFramePid == 0 || _frames.GetStats(_lastFramePid) == null)
        {
            SessStatus.Text = "Nenhum jogo detectado. Abra o jogo, deixe ele na tela por uns segundos e volte aqui (ou use a gravação automática).";
            return;
        }
        StartSession(_lastFramePid);
    }

    /// <summary>Timer de 1 s: grava a sessão em curso ou procura um jogo para começar.</summary>
    private void SessionTick()
    {
        if (_session != null) { RecordSecond(); return; }
        if (BenchArmedTick()) return;

        var fg = _frames.GetForegroundStats();
        if (fg != null && !NotGames.Contains(fg.ProcessName)) _lastFramePid = fg.ProcessId;
        if (fg == null || fg.Fps < 15 || NotGames.Contains(fg.ProcessName) || !ForegroundCoversMonitor())
        {
            if (fg == null) _candidateSecs = 0;
            UpdateSessionStatus();
            return;
        }
        if (fg.ProcessId != _candidatePid) { _candidatePid = fg.ProcessId; _candidateSecs = 0; }
        if (++_candidateSecs >= 10 && _settings.AutoRecordGames) StartSession(fg.ProcessId);
        UpdateSessionStatus();
    }

    private void StartSession(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            string exe = "";
            try { exe = proc.MainModule?.FileName ?? ""; } catch { }
            var gpu = _gpus.FirstOrDefault();
            IntPtr nvml = gpu?.NvmlHandle ?? IntPtr.Zero;
            _pcieProbe = new PcieProbe(nvml);
            var gpuProbe = new GpuProcessProbe();
            _probes = new List<SessionProbe> { new GameProcessProbe(), _pcieProbe, gpuProbe, new BackgroundProcessesProbe(gpuProbe), new NetworkProbe(), new PresentMonProbe(),
                                             new InputProbe(_settings.SessionKeyboard) };
            if (ChkBenchDeep.IsChecked == true) _probes.Add(new DpcProbe());

            _sessionRows = _monitor.Rows.ToList();
            var session = new GameSession(GameName(proc, exe), exe, pid, _sessionRows.Select(SensorColumn).ToList(),
                                          _probes.SelectMany(p => p.Columns).ToList());
            foreach (var probe in _probes)
                try { probe.Start(pid, exe, session.Folder); }
                catch (Exception ex) { session.Note("sonda_falhou", $"{probe.GetType().Name}: {ex.Message}"); }

            _session = session;
            _sessionProc = proc;
            _frames.Present += session.OnPresent;
            BenchOnSessionStart(session, exe);

            // Contexto (RAM, monitor, opções do Windows, disco...) em segundo plano: WMI pode levar 1–2 s e o jogo está rodando.
            IntPtr hwnd = ForegroundPid() == pid ? GetForegroundWindow() : proc.MainWindowHandle;
            string? cpu = InfoValue("Processador"), gpuName = gpu?.Name, driver = nvml != IntPtr.Zero ? Nvml.DriverVersion() : null;
            string folder = session.Folder;
            var inputTest = _settings.LastInputTest;
            string? affinityText = null;
            try
            {
                ulong all = Environment.ProcessorCount >= 64 ? ulong.MaxValue : (1UL << Environment.ProcessorCount) - 1;
                ulong mask = unchecked((ulong)(long)proc.ProcessorAffinity);
                affinityText = (mask & all) == all ? "todos os núcleos" : "threads " + CpuTopology.MaskText(mask);
            }
            catch { /* anti-cheat pode negar a leitura */ }
            _sessionCtx = Task.Run(() =>
            {
                var tweaksOn = WindowsTweaks.All().Where(t => { try { return t.Read() == true; } catch { return false; } }).Select(t => t.Title).ToList();
                var ctx = SessionContextCollector.Collect(exe, hwnd, nvml, cpu, gpuName, driver, tweaksOn);
                ctx.EntradaMedida = inputTest;
                ctx.Afinidade = affinityText;
                try { ctx.Save(folder); } catch { }
                return ctx;
            });
            if (_benchActive)
                _benchRecollect = () => SessionContextCollector.Collect(exe, hwnd, nvml, cpu, gpuName, driver,
                    WindowsTweaks.All().Where(t => { try { return t.Read() == true; } catch { return false; } }).Select(t => t.Title).ToList());
            AddEvent("", "Gravação de sessão", $"{session.Game} · começou", "ok");
        }
        catch (Exception ex)
        {
            _session = null;
            SessStatus.Text = $"Não foi possível começar a gravação: {ex.Message}";
        }
        UpdateSessionStatus();
    }

    private void RecordSecond()
    {
        var s = _session!;
        bool exited;
        try { _sessionProc!.Refresh(); exited = _sessionProc.HasExited; } catch { exited = true; }
        if (exited) { StopSession("o jogo fechou"); return; }
        if (s.Elapsed >= MaxSession) { StopSession("limite de 6 h"); return; }

        var metrics = new double?[_probes.Sum(p => p.Columns.Count)];
        int offset = 0;
        foreach (var probe in _probes)
        {
            try { probe.Fill(metrics, offset); } catch { }
            offset += probe.Columns.Count;
        }

        var gpu = _gpus.FirstOrDefault();
        int? pstate = null; string? perfCap = null; bool thermal = false;
        if (gpu != null && gpu.NvmlHandle != IntPtr.Zero)
        {
            pstate = Nvml.PState(gpu.NvmlHandle);
            if (Nvml.ThrottleReasons(gpu.NvmlHandle) is ulong r) { perfCap = GpuInfo.PerfCap(r); thermal = (r & 0x60) != 0; }
        }
        var limiter = BottleneckAnalyzer.Sample(_monitor, gpu, _frames.GetStats(s.Pid)).Classify();
        bool foreground = ForegroundPid() == s.Pid;

        s.Tick(new SessionSecond(foreground, limiter.ToString(), pstate, perfCap, thermal,
            _monitor.CpuTemperature, MainGpuTemp(), _monitor.CpuPower, MainGpuPower(),
            metrics, _sessionRows.Select(r => r.RawValue).ToList()));
        BenchTick(s);

        if (PageSessions.IsVisible) UpdateSessionStatus();
    }

    /// <param name="wait">true ao fechar o app: termina de salvar antes de o processo acabar.</param>
    private void StopSession(string reason, bool wait = false)
    {
        var s = _session;
        if (s == null) return;
        _frames.Present -= s.OnPresent;
        var bench = BenchSnapshotAndReset(reason);
        _session = null;
        _sessionProc?.Dispose();
        _sessionProc = null;
        var probes = _probes;
        var pcie = _pcieProbe;
        var ctxTask = _sessionCtx;
        _probes = new();
        _pcieProbe = null;
        _sessionCtx = null;
        string? ram = InfoValue("Memória RAM");
        _candidatePid = 0; _candidateSecs = 0;
        UpdateSessionStatus();

        FrameSummary? Save()
        {
            var extra = new Dictionary<string, object?>();
            foreach (var probe in probes)
            {
                try { foreach (var kv in probe.Finish()) extra[kv.Key] = kv.Value; } catch { }
                try { probe.Dispose(); } catch { }
            }
            SessionContext? ctx = null;
            try { if (ctxTask != null && ctxTask.Wait(TimeSpan.FromSeconds(20))) ctx = ctxTask.Result; } catch { }
            if (ctx != null)
            {
                ctx.PcieGeracaoVista = pcie?.MaxGen;
                ctx.PcieLarguraVista = pcie?.MaxWidth;
                extra["cpu"] = ctx.Cpu;
                extra["gpu"] = ctx.Gpu;
                extra["avisos"] = SessionAdvice.Evaluate(ctx).Select(a => a.ToString()).ToList();
                try { ctx.Save(s.Folder); } catch { }
            }
            if (bench != null) BenchApply(bench, s, ctx, extra);
            extra["ram"] = ram;
            var errors = OcCheck.ErrorsSince(s.Start);
            extra["erros_whea"] = errors.Whea;
            extra["resets_driver_video"] = errors.GpuResets;
            extra["ultimo_erro_hardware"] = errors.LastDetail;
            var sum = s.Finish(reason, extra);
            try { SessionAnalyzer.Run(s.Folder); } catch { }
            try { SessionStore.Prune(); } catch { }
            return sum;
        }

        void Done(FrameSummary? sum, Exception? ex)
        {
            AddEvent("", "Gravação de sessão", ex == null && sum != null
                ? $"{s.Game} · {sum.AvgFps:0} FPS, 1% low {sum.Low1Fps:0}" : $"{s.Game} · falha ao salvar: {ex?.Message}", ex == null ? "ok" : "abortado");
            if (PageSessions.IsVisible) { RefreshSessionList(); RefreshBenchProfiles(); }
            if (bench != null) CsBenchStatus.Text = sum != null ? $"Captura salva: {sum.AvgFps:0} fps, 1% low {sum.Low1Fps:0}." : "Falha ao salvar a captura.";
        }

        if (wait)
        {
            try { Save(); } catch { }
            return;
        }
        Task.Run(Save).ContinueWith(t => Dispatcher.BeginInvoke(() => Done(t.IsFaulted ? null : t.Result, t.Exception?.GetBaseException())));
    }

    private void UpdateSessionStatus()
    {
        if (!PageSessions.IsVisible) return;
        if (_session is GameSession s)
        {
            SessStatus.Text = $"Gravando {s.Game} · {s.Elapsed:h\\:mm\\:ss} · {s.FrameCount:N0} frames · {s.Stutters} engasgos";
            BtnSessToggle.Content = "PARAR GRAVAÇÃO";
        }
        else
        {
            SessStatus.Text = _settings.AutoRecordGames
                ? (_candidateSecs > 0 ? $"Jogo detectado, a gravação começa em {Math.Max(0, 10 - _candidateSecs)} s…" : "Aguardando um jogo em tela cheia.")
                : "Gravação automática desligada.";
            BtnSessToggle.Content = "GRAVAR AGORA";
        }
    }

    private void RefreshSessionList()
    {
        var list = SessionStore.List();
        SessList.ItemsSource = list;
        SessTotal.Text = list.Count == 0 ? "Nenhuma sessão gravada ainda."
            : $"{list.Count} sessões · {list.Sum(i => i.Bytes) / 1048576.0:0} MB (as mais antigas são apagadas acima de {SessionStore.MaxTotalBytes >> 30} GB)";
    }

    private void SessOpen_Click(object sender, RoutedEventArgs e)
    {
        string dir = (sender as FrameworkElement)?.Tag is SessionStore.Item i ? i.Folder : GameSession.Root;
        Directory.CreateDirectory(dir);
        ShellRunner.OpenUrl(dir);
    }

    private void SessDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionStore.Item i) return;
        if (MessageBox.Show(this, $"Apagar a sessão de {i.Game} ({i.When})?", "Sessões", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { Directory.Delete(i.Folder, true); } catch (Exception ex) { SessStatus.Text = $"Não foi possível apagar: {ex.Message}"; }
        RefreshSessionList();
    }

    // ---- painel do laudo (uma sessão por vez) e comparação com a anterior do mesmo jogo ----

    private SessionStore.Item? _detail, _compareWith;

    private async void SessLaudo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionStore.Item i) return;
        _detail = i;
        _compareWith = null;
        SessDetail.Visibility = Visibility.Visible;
        SessDetailTitle.Text = $"LAUDO · {i.Game.ToUpperInvariant()} · {i.When}";
        SessDetailLines.ItemsSource = new[] { "Analisando…" };
        SessCompareLines.ItemsSource = null;
        // Sessões antigas (ou interrompidas) ganham o laudo na hora; as novas já vêm com ele.
        var laudo = await Task.Run(() => { try { return SessionAnalyzer.Run(i.Folder); } catch { return null; } });
        SessDetailVerdict.Text = laudo?.Veredito.ToUpperInvariant() ?? "SEM DADOS";
        SessDetailLines.ItemsSource = laudo?.Linhas.Select(l => l.ToString()).ToList() ?? new List<string> { "Esta sessão não tem resumo (foi interrompida antes de salvar)." };
        var previous = SessionStore.List().FirstOrDefault(x => x.Game == i.Game && x.Start < i.Start && x.Folder != i.Folder);
        BtnSessCompare.IsEnabled = previous != null;
        BtnSessCompare.Content = previous != null ? $"COMPARAR COM A ANTERIOR ({previous.Start:dd/MM HH:mm})" : "SEM SESSÃO ANTERIOR DESTE JOGO";
        BtnSessCompare.Tag = previous;
        RefreshSessionList();
        SessDetail.BringIntoView();
    }

    private void BtnSessCompare_Click(object sender, RoutedEventArgs e)
    {
        if (_detail == null || BtnSessCompare.Tag is not SessionStore.Item prev) return;
        _compareWith = prev;
        SessCompareLines.ItemsSource = new[] { $"Comparação: {prev.When} → {_detail.When}" }.Concat(SessionAnalyzer.Compare(prev.Folder, _detail.Folder)).ToList();
    }

    private async void BtnSessDetailClaude_Click(object sender, RoutedEventArgs e)
    {
        if (_detail == null) return;
        await OpenClaudeAsync(_detail.Folder, _compareWith?.Folder);
    }

    private void BtnSessDetailClose_Click(object sender, RoutedEventArgs e)
    {
        SessDetail.Visibility = Visibility.Collapsed;
        _detail = _compareWith = null;
    }

    // ------------------------------------------------------------------ apoio

    private static string SensorColumn(SensorRow r) => $"{r.Hardware} | {r.Name} ({r.Type switch
    {
        SensorType.Temperature => "°C", SensorType.Load or SensorType.Control or SensorType.Level => "%", SensorType.Clock => "MHz",
        SensorType.Power => "W", SensorType.Voltage => "V", SensorType.Current => "A", SensorType.Fan => "RPM", SensorType.Data => "GB",
        SensorType.SmallData => "MB", SensorType.Throughput => "B/s", SensorType.Frequency => "Hz", SensorType.Energy => "mWh",
        _ => r.Type.ToString()
    }})";

    private string GameName(Process proc, string exe)
    {
        var lib = _libGames.FirstOrDefault(g => g.InstallDir.Length > 3 && exe.StartsWith(g.InstallDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        if (lib != null) return lib.Name;
        try
        {
            var v = FileVersionInfo.GetVersionInfo(exe);
            foreach (var n in new[] { v.ProductName, v.FileDescription })
                if (!string.IsNullOrWhiteSpace(n) && n.Length < 60) return n.Trim();
        }
        catch { }
        return proc.ProcessName;
    }

    /// <summary>Valor de uma categoria da página Sistema (ex.: "Processador"), para o resumo da sessão.</summary>
    private string? InfoValue(string header) =>
        (InfoCategories.ItemsSource as IEnumerable<InfoNode>)?.FirstOrDefault(n => n.Header == header)?.Children.FirstOrDefault() is InfoNode c
            ? (c.Value is string v && v.Length > 0 ? $"{c.Header}: {v}" : c.Header) : null;

    private static int ForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return (int)pid;
    }

    /// <summary>A janela em primeiro plano ocupa o monitor inteiro (tela cheia ou janela sem borda), como um jogo.</summary>
    private static bool ForegroundCoversMonitor()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
}
