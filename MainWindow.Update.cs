using System.Windows;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Atualização automática (pre-releases "cs2-N" do GitHub) e o botão de relatar problema na barra de título.</summary>
public partial class MainWindow
{
    private UpdateInfo? _update;
    private DispatcherTimer? _updateTimer;
    private bool _updating;

    /// <summary>Chamado depois que a janela carrega: limpa sobras, envia relatórios pendentes e procura versão nova (e de novo a cada 6 h).</summary>
    private async void InitUpdates()
    {
        Updater.CleanupOld();
        _ = Task.Run(ErrorReporter.SendPendingAsync);
        if (Updater.IsDevBuild) return;
        await Task.Delay(TimeSpan.FromSeconds(15)); // não disputa com a carga do hardware na abertura
        await CheckUpdateAsync(quiet: true);
        _updateTimer = new DispatcherTimer(TimeSpan.FromHours(6), DispatcherPriority.Background, async (_, _) => await CheckUpdateAsync(quiet: true), Dispatcher);
        _updateTimer.Start();
    }

    private async Task CheckUpdateAsync(bool quiet)
    {
        try
        {
            var u = await Updater.CheckAsync();
            if (u == null) return;
            bool isNew = _update?.Build != u.Build;
            _update = u;
            BtnUpdate.Content = $"ATUALIZAR ({u.Tag})";
            BtnUpdate.Visibility = Visibility.Visible;
            if (isNew) Toast("Versão nova", $"{u.Tag} disponível. Clique em ATUALIZAR no alto da janela.", "");
        }
        catch (Exception ex) { if (!quiet) MessageBox.Show(this, "Não foi possível verificar: " + ex.Message, "Atualização"); ErrorReporter.Log("atualização (verificar)", ex); }
    }

    private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not UpdateInfo u || _updating) return;
        if (_session != null || _captureArmed)
        {
            MessageBox.Show(this, "Há uma gravação em curso. Termine ou pare a gravação antes de atualizar.", "Atualização");
            return;
        }
        if (MessageBox.Show(this, $"Instalar {u.Tag} agora? O RESET EXPLODE fecha e abre de novo sozinho.\n\nNovidades: {u.PageUrl}",
                            "Atualização", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _updating = true;
        BtnUpdate.IsEnabled = false;
        try
        {
            await Updater.ApplyAsync(u, text => Dispatcher.BeginInvoke(() => BtnUpdate.Content = text.ToUpperInvariant()));
            Close();
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ErrorReporter.Log("atualização (instalar)", ex);
            MessageBox.Show(this, "A atualização falhou e nada foi trocado: " + ex.Message, "Atualização", MessageBoxButton.OK, MessageBoxImage.Warning);
            BtnUpdate.Content = $"ATUALIZAR ({u.Tag})";
            BtnUpdate.IsEnabled = true;
            _updating = false;
        }
    }

    private void BtnProblem_Click(object sender, RoutedEventArgs e)
    {
        string? last = null;
        try { last = SessionStore.List().FirstOrDefault()?.Folder; } catch { }
        new ProblemReportWindow(_settings, last) { Owner = this }.ShowDialog();
    }
}
