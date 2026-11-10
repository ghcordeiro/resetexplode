using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Modo CS automático: acompanha o cs2.exe a cada 2 s, aplica ao abrir e desfaz 6 s depois de fechar.</summary>
public partial class MainWindow
{
    private DispatcherTimer? _gmTimer;
    private bool _gmApplied, _gmBusy;
    private DateTime? _gmMissingSince;

    private void InitGameMode()
    {
        // Sobrou estado de uma vez em que o app fechou com o jogo aberto: desfaz já (se o jogo estiver aberto, reaplica no próximo ciclo).
        if (GameMode.Active) _ = Task.Run(GameMode.Undo);
        _gmTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => GameModeTick(), Dispatcher);
        _gmTimer.Start();
    }

    private async void GameModeTick()
    {
        if (_gmBusy) return;
        var o = _settings.GameMode;
        int pid = 0;
        var procs = Process.GetProcessesByName("cs2");
        try { if (procs.Length > 0) pid = procs[0].Id; } finally { foreach (var p in procs) p.Dispose(); }

        if (!_gmApplied && pid != 0 && o.Enabled)
        {
            _gmApplied = true; _gmBusy = true;
            bool keepApps = (SessProfile?.Text ?? "").Contains("uso real", StringComparison.OrdinalIgnoreCase);
            var log = await Task.Run(() => { try { return GameMode.Apply(o, pid, keepApps); } catch (Exception ex) { ErrorReporter.Log("modo CS", ex); return new List<string> { "falhou: " + ex.Message }; } });
            _gmBusy = false;
            GameModeStatus.Text = $"CS2 aberto às {DateTime.Now:HH:mm}: " + (log.Count > 0 ? string.Join("; ", log) : "nada a mudar") + ".";
            AddEvent("", "Modo CS", log.Count > 0 ? string.Join("; ", log) : "aplicado", "ok");
        }
        else if (_gmApplied && pid == 0)
        {
            _gmMissingSince ??= DateTime.Now;
            if (DateTime.Now - _gmMissingSince < TimeSpan.FromSeconds(6)) return; // o jogo pode reabrir (troca de modo de vídeo)
            _gmApplied = false; _gmMissingSince = null; _gmBusy = true;
            var log = await Task.Run(GameMode.Undo);
            _gmBusy = false;
            GameModeStatus.Text = $"CS2 fechado às {DateTime.Now:HH:mm}: " + (log.Count > 0 ? string.Join("; ", log) : "nada a desfazer") + ".";
        }
        else if (pid != 0) _gmMissingSince = null;
    }

    private void LoadGameModeOptions()
    {
        var o = _settings.GameMode;
        ChkGmEnabled.IsChecked = o.Enabled;
        ChkGmDesktop.IsChecked = o.Desktop;
        ChkGmClose.IsChecked = o.CloseApps;
        ChkGmMemory.IsChecked = o.CleanMemory;
        ChkGmServices.IsChecked = o.StopServices;
        ChkGmPriority.IsChecked = o.HighPriority;
        ChkGmPower.IsChecked = o.PowerPlan;
    }

    private void GameModeOption_Click(object sender, RoutedEventArgs e)
    {
        var o = _settings.GameMode;
        o.Enabled = ChkGmEnabled.IsChecked == true;
        o.Desktop = ChkGmDesktop.IsChecked == true;
        o.CloseApps = ChkGmClose.IsChecked == true;
        o.CleanMemory = ChkGmMemory.IsChecked == true;
        o.StopServices = ChkGmServices.IsChecked == true;
        o.HighPriority = ChkGmPriority.IsChecked == true;
        o.PowerPlan = ChkGmPower.IsChecked == true;
        try { _settings.Save(); } catch { }
    }
}
