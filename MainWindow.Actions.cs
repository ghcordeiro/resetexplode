using System.IO;
using System.Windows;

namespace ResetExplode;

/// <summary>Ações por jogo (afinidade, perfil do driver NVIDIA) e a limpeza do cache de shaders no RESET OTIMIZA.</summary>
public partial class MainWindow
{
    private CpuTopology.Info? _cpuTopo;

    /// <summary>Opções do perfil que dependem do hardware: núcleos da CPU e o cartão da NVIDIA.</summary>
    private async void ShowHardwareProfileOptions(LibGame g, GameProfile p)
    {
        _cpuTopo ??= await Task.Run(CpuTopology.Read);
        LAffVCache.IsEnabled = CpuTopology.MaskFor(_cpuTopo, CpuTopology.ModeVCache) != null || p.Affinity == CpuTopology.ModeVCache;
        LAffPCores.IsEnabled = CpuTopology.MaskFor(_cpuTopo, CpuTopology.ModePCores) != null || p.Affinity == CpuTopology.ModePCores;
        LAffinityInfo.Text = CpuTopology.Describe(_cpuTopo) + " Vale enquanto o jogo roda aberto pelo JOGAR.";

        bool nv = await Task.Run(() => NvidiaProfiles.Available);
        LNvidiaBox.Visibility = nv ? Visibility.Visible : Visibility.Collapsed;
        if (!nv || _lg != g) return;
        LNvFps.Text = p.NvFpsLimit > 0 ? p.NvFpsLimit.ToString() : "";
        LNvLowLat.IsChecked = p.NvLowLatency;
        LNvMaxPerf.IsChecked = p.NvMaxPerf;
        SetChip(LNvVsyncPanel, p.NvVsync);
        var mon = await Task.Run(SessionContextCollector.PrimaryMonitor);
        LNvSuggest.Text = mon is { HzAtual: > 60 } m ? $"sugestão: {m.HzAtual - 3} (monitor de {m.HzAtual} Hz, fica dentro do G-Sync/FreeSync)" : "";
        string? exe = GameExeName(g, p);
        if (exe == null) { LNvStatus.Text = "Escolha o .exe do jogo (ESCOLHER O .EXE) para usar o perfil do driver."; return; }
        LNvStatus.Text = "Lendo o driver…";
        var state = await Task.Run(() => { try { return (NvidiaProfiles.Read(exe), (string?)null); } catch (Exception ex) { return (null, ex.Message); } });
        if (_lg != g) return;
        LNvStatus.Text = state.Item2 ?? (state.Item1 is { } st
            ? $"No driver ({exe}, perfil \"{st.Profile}\"): " + (st.FpsLimit is uint f && f > 0 ? $"limite de {f} FPS" : "sem limite de FPS") +
              (st.Prerendered == 1 ? ", baixa latência ligada" : ", baixa latência como o jogo pedir") +
              (st.PState == 1 ? ", energia em desempenho máximo" : "") +
              (st.VSync == NvidiaProfiles.VSyncOff ? ", V-Sync desligado" : st.VSync == NvidiaProfiles.VSyncOn ? ", V-Sync ligado" : "") + "."
            : $"No driver: {exe} sem perfil (usa as opções globais da NVIDIA).");
    }

    private string? GameExeName(LibGame g, GameProfile p)
    {
        string? exe = p.ExePath ?? g.ExePath ?? _exeGuess.GetValueOrDefault(g.Key);
        return exe != null ? Path.GetFileName(exe) : null;
    }

    private async void BtnNvApply_Click(object sender, RoutedEventArgs e)
    {
        if (_lg is not LibGame g || _lp is not GameProfile p) return;
        string? exe = GameExeName(g, p);
        if (exe == null) { LNvStatus.Text = "Escolha o .exe do jogo primeiro (ESCOLHER O .EXE)."; return; }
        string txt = LNvFps.Text.Trim();
        if (!(txt.Length == 0 || (uint.TryParse(txt, out uint n) && (n == 0 || n is >= 20 and <= 1000))))
        {
            LNvStatus.Text = "Limite de FPS entre 20 e 1000 (ou vazio para sem limite).";
            return;
        }
        uint fps = txt.Length == 0 ? 0 : uint.Parse(txt);
        bool low = LNvLowLat.IsChecked == true;
        LNvStatus.Text = "Gravando no driver…";
        string msg = await Task.Run(() => { try { return NvidiaProfiles.Apply(exe, fps, low); } catch (Exception ex) { return ex.Message; } });
        bool maxPerf = LNvMaxPerf.IsChecked == true;
        string vs = CheckedTag(LNvVsyncPanel) ?? "0";
        uint vsValue = vs switch { "off" => NvidiaProfiles.VSyncOff, "on" => NvidiaProfiles.VSyncOn, _ => 0u };
        bool touchExtra = maxPerf || vsValue != 0 || p.NvMaxPerf || p.NvVsync != "0";
        if (touchExtra)
            msg += " " + await Task.Run(() => { try { return NvidiaProfiles.ApplyExtra(exe, maxPerf, vsValue); } catch (Exception ex) { return ex.Message; } });
        p.NvFpsLimit = (int)fps;
        p.NvLowLatency = low;
        p.NvMaxPerf = maxPerf;
        p.NvVsync = vs;
        try { _settings.Save(); } catch { }
        if (_lg == g) LNvStatus.Text = msg;
    }

    private async void BtnNvRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_lg is not LibGame g || _lp is not GameProfile p) return;
        string? exe = GameExeName(g, p);
        if (exe == null) { LNvStatus.Text = "Escolha o .exe do jogo primeiro (ESCOLHER O .EXE)."; return; }
        LNvStatus.Text = "Removendo do driver…";
        string msg = await Task.Run(() => { try { return NvidiaProfiles.RemoveAll(exe); } catch (Exception ex) { return ex.Message; } });
        p.NvFpsLimit = 0; p.NvLowLatency = false; p.NvMaxPerf = false; p.NvVsync = "0";
        try { _settings.Save(); } catch { }
        if (_lg == g) { LNvFps.Text = ""; LNvLowLat.IsChecked = false; LNvMaxPerf.IsChecked = false; SetChip(LNvVsyncPanel, "0"); LNvStatus.Text = msg; }
    }

    // ---------------- RESET OTIMIZA · cache de shaders ----------------

    private List<ShaderCache.Location> _shaderDirs = new();

    private async void RefreshShaderInfo()
    {
        ShaderInfo.Text = "Medindo…";
        _shaderDirs = await Task.Run(ShaderCache.Locations);
        long total = _shaderDirs.Sum(d => d.Bytes);
        ShaderInfo.Text = _shaderDirs.Count == 0 ? "Nenhuma pasta de cache encontrada."
            : string.Join(" · ", _shaderDirs.Where(d => d.Bytes > 0).Select(d => $"{d.Name} {DriveCard.Fmt(d.Bytes)}")) + $"  (total {DriveCard.Fmt(total)})";
        BtnShaderClean.IsEnabled = total > 0;
    }

    private async void BtnShaderClean_Click(object sender, RoutedEventArgs e)
    {
        if (_shaderDirs.Count == 0) return;
        var ok = MessageBox.Show(this,
            "Apagar o cache de shaders? Os jogos vão recompilar os shaders: as primeiras partidas de cada jogo podem engasgar um pouco " +
            "(ou mostrar \"compilando shaders\"). Feche os jogos abertos antes.\n\nContinuar?",
            "Cache de shaders", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;
        BtnShaderClean.IsEnabled = false;
        var dirs = _shaderDirs.Select(d => d.Path).ToList();
        var (freed, skipped) = await Task.Run(() => ShaderCache.Clean(dirs));
        RefreshShaderInfo();
        ShaderInfo.Text = $"Liberados {DriveCard.Fmt(freed)}" + (skipped > 0 ? $"; {skipped} arquivo(s) em uso ficaram (feche os jogos e limpe de novo)." : ".");
    }
}
