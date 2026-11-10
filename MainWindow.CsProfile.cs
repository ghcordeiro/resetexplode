using System.Diagnostics;
using System.Windows;

namespace ResetExplode;

/// <summary>RESET OTIMIZA › PC de CS: aplicar o perfil inteiro (com ponto de restauração) e preparar a máquina antes de jogar.</summary>
public partial class MainWindow
{
    private async void BtnCsApply_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        var pending = _tweaks.Where(t => t.Warning == null && !CsProfile.ExperimentOnly.Contains(t.Id)).ToList();
        var r = MessageBox.Show(this,
            "O RESET EXPLODE vai:\n\n" +
            "1. Criar um ponto de restauração do Windows.\n" +
            "2. Ligar todos os ajustes seguros da lista (serviços, rede, inicialização, energia, tarefas de telemetria…).\n\n" +
            "O valor original de cada um é guardado ANTES da mudança. RESTAURAR ORIGINAIS desfaz tudo. Alguns só valem depois de reiniciar.\n" +
            "Windows Update, Defender, TPM, Secure Boot, VBS, áudio e rede (DHCP/DNS) não são tocados.\n\nContinuar?",
            "PC de CS", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        BtnCsApply.IsEnabled = false;
        BtnCsApply.Content = "APLICANDO…";
        OptResult.Text = "Criando o ponto de restauração (pode levar um minuto)…";
        try
        {
            var (ok, msg) = await Task.Run(CsProfile.CreateRestorePoint);
            OptResult.Text = ok ? "Ponto de restauração criado. Aplicando…" : $"Sem ponto de restauração ({msg}). Aplicando mesmo assim; RESTAURAR ORIGINAIS desfaz.";
            var (applied, failed) = await Task.Run(() => CsProfile.ApplyAll(pending, _settings));
            foreach (var t in _tweaks) t.Refresh();
            bool restart = pending.Any(t => t.NeedsRestart && t.IsOn);
            OptResult.Text = (ok ? "Ponto de restauração criado. " : $"Sem ponto de restauração ({msg}). ") +
                             $"{applied.Count} ajuste(s) ligado(s)" + (failed.Count > 0 ? $", {failed.Count} falharam: {string.Join("; ", failed)}" : "") +
                             (restart ? ". Reinicie o PC para valer tudo." : ".");
        }
        catch (Exception ex) { OptResult.Text = $"Falhou: {ex.Message}"; }
        finally { BtnCsApply.IsEnabled = true; BtnCsApply.Content = "APLICAR PC DE CS"; }
    }

    /// <summary>Marca para fechar tudo que não está na lista permitida e roda a limpeza de sempre (que pede confirmação mostrando a lista).</summary>
    private void BtnPrepare_Click(object sender, RoutedEventArgs e)
    {
        RefreshApps();
        foreach (var a in _apps) a.Selected = !StartupPolicy.IsAllowed(a.ProcessName, a.Title);
        BtnOptimize_Click(sender, e);

        string[] need = { "steam", "discord" };
        var missing = need.Where(n => Process.GetProcessesByName(n).Length == 0).ToList();
        if (!Process.GetProcesses().Any(p => { try { return p.ProcessName.StartsWith("razer", StringComparison.OrdinalIgnoreCase) || p.ProcessName.StartsWith("synapse", StringComparison.OrdinalIgnoreCase); } catch { return false; } }))
            missing.Add("Razer Synapse");
        if (missing.Count > 0) OptResult.Text += $" Não estão abertos: {string.Join(", ", missing)}.";
    }

    // ---- overlays e aceleração ----

    private void RefreshOverlays()
    {
        try { OverlayList.ItemsSource = OverlayCheck.Items(); }
        catch (Exception ex) { OverlayStatus.Text = "Falhou: " + ex.Message; ErrorReporter.Log("overlays", ex); }
    }

    private void BtnOverlayCheck_Click(object sender, RoutedEventArgs e) { OverlayStatus.Text = ""; RefreshOverlays(); }

    private void OverlayFix_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        try { OverlayCheck.Fix(id); OverlayStatus.Text = "Feito. Abra o programa de novo e meça."; }
        catch (Exception ex) { OverlayStatus.Text = ex.Message; }
        RefreshOverlays();
    }

    private void OverlayRestore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id) return;
        try { OverlayCheck.Restore(id); OverlayStatus.Text = "Restaurado."; }
        catch (Exception ex) { OverlayStatus.Text = ex.Message; }
        RefreshOverlays();
    }

    // ---- apps pré-instalados ----

    private List<BloatApp> _bloat = new();

    private async void BtnBloatList_Click(object sender, RoutedEventArgs e)
    {
        BloatStatus.Text = "Listando…";
        try
        {
            _bloat = await Task.Run(Debloat.Installed);
            BloatList.ItemsSource = _bloat;
            BloatStatus.Text = _bloat.Count == 0 ? "Nenhum app da lista instalado." : $"{_bloat.Count} app(s) da lista instalados. Desmarque o que quiser manter.";
        }
        catch (Exception ex) { BloatStatus.Text = "Falhou: " + ex.Message; ErrorReporter.Log("debloat (listar)", ex); }
    }

    private async void BtnBloatRemove_Click(object sender, RoutedEventArgs e)
    {
        var sel = _bloat.Where(a => a.Selected).ToList();
        if (sel.Count == 0) { BloatStatus.Text = "Clique em LISTAR e marque os apps."; return; }
        if (MessageBox.Show(this, "Remover para o seu usuário:\n\n" + string.Join("\n", sel.Select(a => "  • " + a.Title)) +
                            "\n\nPara voltar, use REINSTALAR PELA STORE. Continuar?", "Apps pré-instalados", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        BtnBloatRemove.IsEnabled = false;
        BloatStatus.Text = "Removendo…";
        try
        {
            var (removed, failed) = await Task.Run(() => Debloat.Remove(sel));
            BloatStatus.Text = $"{removed.Count} removido(s)." + (failed.Count > 0 ? " Não removidos: " + string.Join("; ", failed) : "");
            _bloat = await Task.Run(Debloat.Installed);
            BloatList.ItemsSource = _bloat;
        }
        catch (Exception ex) { BloatStatus.Text = "Falhou: " + ex.Message; ErrorReporter.Log("debloat (remover)", ex); }
        finally { BtnBloatRemove.IsEnabled = true; }
    }

    private void BtnBloatRestore_Click(object sender, RoutedEventArgs e)
    {
        var removed = Debloat.LoadRemoved();
        if (removed.Count == 0) { BloatStatus.Text = "Nenhum app foi removido pelo RESET EXPLODE."; return; }
        var pick = removed.First();
        Debloat.OpenInStore(pick.Key);
        removed.Remove(pick.Key);
        try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppSettings.Folder, "debloat-removidos.json"), System.Text.Json.JsonSerializer.Serialize(removed)); } catch { }
        BloatStatus.Text = $"Store aberta em \"{pick.Value}\": clique em Obter. Faltam {removed.Count}.";
    }

    private async void BtnTimers_Click(object sender, RoutedEventArgs e)
    {
        TimerLines.ItemsSource = new[] { "Lendo…" };
        try { TimerLines.ItemsSource = await Task.Run(SystemTimers.Diagnose); }
        catch (Exception ex) { TimerLines.ItemsSource = new[] { "Falhou: " + ex.Message }; }
    }

    // ---- OneDrive, dispositivos e orientações manuais ----

    private static readonly string[] ManualTips =
    {
        "Steam › Configurações › Downloads: desmarque \"Permitir downloads durante o jogo\". Evita disco e rede disputando com o CS2.",
        "Steam › Configurações › Interface: desmarque \"Ativar a aceleração por GPU nas visualizações web\" (a Steam deixa de usar a placa por baixo do jogo).",
        "Painel de Controle NVIDIA › Gerenciar configurações 3D › Tamanho do cache de shaders: Ilimitado (ou 10 GB). Evita recompilar shaders e as travadas disso.",
        "CS2 › Vídeo › NVIDIA Reflex Low Latency: Ligado + Boost. Mantém o clock da GPU alto mesmo com a CPU como limite.",
        "CS2 › opções de inicialização: deixe só o que o app precisa (-condebug para o benchmark, +fps_max se testar). A Valve diz que a melhor opção é nenhuma; -high e -threads costumam piorar.",
        "Notificações: ligue o \"Não perturbe\" automático ao jogar e em tela cheia (botão abaixo). Notificação por cima do jogo tira o foco.",
        "Razer Synapse: se o seu mouse guarda o DPI na memória interna, grave o DPI e feche o Synapse durante o jogo. Meça como passo \"sem synapse\" da matriz.",
        "Microsoft Edge e Chrome: não deixe abertos durante a partida; o Modo CS automático fecha o que não está na lista permitida.",
    };

    private void InitManualTips() => ManualList.ItemsSource = ManualTips;

    private void BtnOpenNotifications_Click(object sender, RoutedEventArgs e) => ShellRunner.OpenUrl("ms-settings:notifications");
    private void BtnOpenCoreIsolation_Click(object sender, RoutedEventArgs e) => ShellRunner.OpenUrl("windowsdefender://coreisolation");

    private void BtnOneDrive_Click(object sender, RoutedEventArgs e)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string? setup = new[] { System.IO.Path.Combine(win, "SysWOW64", "OneDriveSetup.exe"), System.IO.Path.Combine(win, "System32", "OneDriveSetup.exe") }.FirstOrDefault(System.IO.File.Exists);
        if (setup == null) { DevStatus.Text = "OneDrive não encontrado (já desinstalado?)."; return; }
        if (MessageBox.Show(this, "Desinstalar o OneDrive? Arquivos que só estão na nuvem continuam no site. Para voltar: winget install Microsoft.OneDrive.",
                            "OneDrive", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            foreach (var p in Process.GetProcessesByName("OneDrive")) { try { p.Kill(); } catch { } p.Dispose(); }
            Process.Start(new ProcessStartInfo(setup, "/uninstall") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
            DevStatus.Text = "OneDrive sendo desinstalado.";
        }
        catch (Exception ex) { DevStatus.Text = "Falhou: " + ex.Message; ErrorReporter.Log("onedrive", ex); }
    }

    private List<UnusedDevice> _devices = new();

    private async void BtnDevScan_Click(object sender, RoutedEventArgs e)
    {
        DevStatus.Text = "Listando…";
        try
        {
            _devices = await Task.Run(DeviceCleanup.Scan);
            DevList.ItemsSource = _devices;
            DevStatus.Text = (_devices.Count == 0 ? "Nenhum candidato." : $"{_devices.Count} candidato(s); marque só o que você realmente não usa.") + (DeviceCleanup.DisabledCount > 0 ? $" {DeviceCleanup.DisabledCount} já desabilitado(s) pelo app." : "");
        }
        catch (Exception ex) { DevStatus.Text = "Falhou: " + ex.Message; ErrorReporter.Log("dispositivos (listar)", ex); }
    }

    private async void BtnDevDisable_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        var sel = _devices.Where(d => d.Selected).ToList();
        if (sel.Count == 0) { DevStatus.Text = "Marque algum dispositivo."; return; }
        if (MessageBox.Show(this, "Desabilitar:\n\n" + string.Join("\n", sel.Select(d => "  • " + d.Title)) + "\n\nREABILITAR TODOS desfaz. Continuar?",
                            "Dispositivos", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var (done, failed) = await Task.Run(() => DeviceCleanup.Disable(sel));
        DevStatus.Text = $"{done.Count} desabilitado(s)." + (failed.Count > 0 ? " Falharam: " + string.Join("; ", failed) : "");
        _devices = await Task.Run(DeviceCleanup.Scan);
        DevList.ItemsSource = _devices;
    }

    private async void BtnDevEnable_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;
        var (ok, failed) = await Task.Run(DeviceCleanup.EnableAll);
        DevStatus.Text = $"{ok} reabilitado(s)." + (failed.Count > 0 ? " Falharam: " + string.Join("; ", failed) : "");
    }
}
