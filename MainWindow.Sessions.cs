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
    private TimeSpan _sessionCpu0;
    private DateTime _sessionCpuAt;
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
        RefreshSessionList();
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
            _sessionRows = _monitor.Rows.ToList();
            _session = new GameSession(GameName(proc, exe), exe, pid, _sessionRows.Select(SensorColumn).ToList());
            _frames.Present += _session.OnPresent;
            _sessionProc = proc;
            _sessionCpu0 = proc.TotalProcessorTime;
            _sessionCpuAt = DateTime.Now;
            AddEvent("", "Gravação de sessão", $"{_session.Game} · começou", "ok");
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
        var p = _sessionProc!;
        bool exited;
        try { p.Refresh(); exited = p.HasExited; } catch { exited = true; }
        if (exited) { StopSession("o jogo fechou"); return; }
        if (s.Elapsed >= MaxSession) { StopSession("limite de 6 h"); return; }

        double? cpuPct = null, ramMb = null; int? threads = null;
        try
        {
            var now = DateTime.Now;
            var cpu = p.TotalProcessorTime;
            double wall = (now - _sessionCpuAt).TotalMilliseconds;
            if (wall > 0) cpuPct = 100.0 * (cpu - _sessionCpu0).TotalMilliseconds / (wall * Environment.ProcessorCount);
            _sessionCpu0 = cpu; _sessionCpuAt = now;
            ramMb = p.WorkingSet64 / 1048576.0;
            threads = p.Threads.Count;
        }
        catch { }

        var gpu = _gpus.FirstOrDefault();
        int? pstate = null; string? perfCap = null; bool thermal = false;
        if (gpu != null && gpu.NvmlHandle != IntPtr.Zero)
        {
            pstate = Nvml.PState(gpu.NvmlHandle);
            if (Nvml.ThrottleReasons(gpu.NvmlHandle) is ulong r) { perfCap = GpuInfo.PerfCap(r); thermal = (r & 0x60) != 0; }
        }
        var limiter = BottleneckAnalyzer.Sample(_monitor, gpu, _frames.GetStats(s.Pid)).Classify();
        bool foreground = ForegroundPid() == s.Pid;

        s.Tick(new SessionSecond(foreground, limiter.ToString(), pstate, perfCap, thermal, cpuPct, ramMb, threads,
            _monitor.CpuTemperature, MainGpuTemp(), _monitor.CpuPower, MainGpuPower(),
            _sessionRows.Select(r => r.RawValue).ToList()));

        if (PageSessions.IsVisible) UpdateSessionStatus();
    }

    private void StopSession(string reason)
    {
        var s = _session;
        if (s == null) return;
        _frames.Present -= s.OnPresent;
        _session = null;
        _sessionProc?.Dispose();
        _sessionProc = null;
        try
        {
            var errors = OcCheck.ErrorsSince(s.Start);
            var sum = s.Finish(reason, new Dictionary<string, object?>
            {
                ["cpu"] = InfoValue("Processador"),
                ["gpu"] = _gpus.FirstOrDefault()?.Name,
                ["ram"] = InfoValue("Memória RAM"),
                ["erros_whea"] = errors.Whea,
                ["resets_driver_video"] = errors.GpuResets,
                ["ultimo_erro_hardware"] = errors.LastDetail,
            });
            AddEvent("", "Gravação de sessão", $"{s.Game} · {sum.AvgFps:0} FPS, 1% low {sum.Low1Fps:0}", "ok");
        }
        catch (Exception ex) { AddEvent("", "Gravação de sessão", $"{s.Game} · falha ao salvar: {ex.Message}", "abortado"); }
        try { SessionStore.Prune(); } catch { }
        _candidatePid = 0; _candidateSecs = 0;
        if (PageSessions.IsVisible) RefreshSessionList();
        UpdateSessionStatus();
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

    private async void SessClaude_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not SessionStore.Item i) return;
        await OpenClaudeAsync(i.Folder);
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
