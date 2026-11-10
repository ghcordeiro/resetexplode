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
}
