using System.Globalization;
using System.IO;
using System.Windows;

namespace ResetExplode;

/// <summary>RESET MAKER · Atraso: testador com Arduino (clique até a tela e PC até a tela) e o modo câmera.</summary>
public partial class MainWindow
{
    private CancellationTokenSource? _latCts;

    private void InitLatency()
    {
        var ports = SerialLatencyLink.Ports();
        LatPorts.ItemsSource = ports;
        LatPorts.SelectedItem = ports.FirstOrDefault(p => p.Port == _settings.LatencyPort) ?? ports.FirstOrDefault(p => p.LikelyArduino) ?? ports.FirstOrDefault();
        if (ports.Count == 0) LatStatus.Text = "Nenhuma porta COM: ligue o Arduino no USB.";
        ShowLastLatency();
    }

    private void ShowLastLatency()
    {
        var r = _settings.LastLatency;
        if (r == null) { LatResult.Text = ""; return; }
        LatResult.Text = $"Último resultado ({r.Data:dd/MM HH:mm}): " +
                         string.Join(" · ", new[]
                         {
                             r.CliqueTelaMs is double c ? $"clique até a tela {c:0.0} ms" : null,
                             r.PcTelaMs is double p ? $"PC até a tela {p:0.0} ms" : null,
                             r.EntradaMs is double e ? $"mouse + Windows ≈ {e:0.0} ms" : null,
                             r.RespostaPixelMs is double px ? $"resposta do pixel {px:0.0} ms" : null,
                         }.OfType<string>()) + ". Vai junto no contexto de cada sessão gravada.";
    }

    private async void BtnLatFind_Click(object sender, RoutedEventArgs e)
    {
        var ports = SerialLatencyLink.Ports();
        LatPorts.ItemsSource = ports;
        LatStatus.Text = "Procurando o testador…";
        string? found = await SerialLatencyLink.FindAsync(ports, CancellationToken.None);
        LatPorts.SelectedItem = ports.FirstOrDefault(p => p.Port == found) ?? LatPorts.SelectedItem;
        LatStatus.Text = found != null ? $"Testador encontrado em {found}." :
            "Testador não encontrado. Confira se o programa ResetLatencia foi carregado na placa e se o monitor serial da Arduino IDE está fechado; ou escolha a porta na lista.";
        if (found != null) { _settings.LatencyPort = found; try { _settings.Save(); } catch { } }
    }

    private void BtnLatClick_Click(object sender, RoutedEventArgs e) => RunLatency(LatencyMode.Click);
    private void BtnLatDisplay_Click(object sender, RoutedEventArgs e) => RunLatency(LatencyMode.Display);

    private async void RunLatency(LatencyMode mode)
    {
        if (_latCts != null) return;
        if (LatPorts.SelectedItem is not SerialLatencyLink.PortInfo port) { LatStatus.Text = "Escolha a porta do testador."; return; }
        _latCts = new CancellationTokenSource();
        BtnLatClick.IsEnabled = BtnLatDisplay.IsEnabled = false;
        LatStatus.Text = "Abrindo o testador…";
        SerialLatencyLink? link = null;
        LatencyWindow? screen = null;
        try
        {
            link = new SerialLatencyLink(port.Port);
            await Task.Delay(300);
            if (!LatencyTester.IsTester(await link.CommandAsync("PING", 800, _latCts.Token)))
                throw new InvalidOperationException($"{port.Port} não respondeu como o testador. Carregue o programa ResetLatencia na placa.");
            _settings.LatencyPort = port.Port;

            screen = new LatencyWindow { WhiteOnClick = mode == LatencyMode.Click };
            var cts = _latCts;
            screen.Closed += () => cts.Cancel(); // ESC
            screen.Start();
            var tester = new LatencyTester(link, screen);
            var stats = await Task.Run(() => tester.RunAsync(mode, 50,
                (i, ms) => Dispatcher.BeginInvoke(() => LatStatus.Text = $"Medição {i}/50" + (ms is double m ? $": {m:0.0} ms" : ": sem resposta")), cts.Token));

            var rec = _settings.LastLatency ?? new LatencyRecord();
            rec.Data = DateTime.Now;
            rec.Metodo = "arduino";
            if (mode == LatencyMode.Click) { rec.CliqueTelaMs = Math.Round(stats.MedianMs, 1); rec.RespostaPixelMs = stats.RiseMedianMs is double r ? Math.Round(r, 1) : rec.RespostaPixelMs; }
            else rec.PcTelaMs = Math.Round(stats.MedianMs, 1);
            _settings.LastLatency = rec;
            LatStatus.Text = stats.Text;
            AddEvent("", "Teste de atraso", stats.Text, "ok");
        }
        catch (OperationCanceledException) { LatStatus.Text = "Teste cancelado."; }
        catch (Exception ex) { LatStatus.Text = "Não foi possível testar: " + ex.Message; }
        finally
        {
            screen?.Dispose();
            link?.Dispose();
            _latCts = null;
            BtnLatClick.IsEnabled = BtnLatDisplay.IsEnabled = true;
            try { _settings.Save(); } catch { }
            ShowLastLatency();
            Activate();
        }
    }

    private void BtnLatCamera_Click(object sender, RoutedEventArgs e)
    {
        // Modo câmera: a pessoa clica; branco enquanto o botão está apertado. ESC fecha.
        var screen = new LatencyWindow { WhiteOnClick = true, BlackOnRelease = true };
        screen.Closed += () => Dispatcher.BeginInvoke(() => { screen.Dispose(); Activate(); });
        try { screen.Start(); }
        catch (Exception ex) { LatCamResult.Text = "Não foi possível abrir a tela de teste: " + ex.Message; }
    }

    private void LatCam_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (LatCamResult == null) return;
        if (double.TryParse(LatCamFrames.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double frames) && frames > 0 &&
            double.TryParse(LatCamFps.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double fps) && fps >= 30)
        {
            double ms = frames * 1000 / fps;
            LatCamResult.Text = $"≈ {ms:0} ms do clique até a tela (±{1000 / fps:0} ms). Faça 5 ou mais medições e use a do meio.";
            var rec = _settings.LastLatency ?? new LatencyRecord();
            if (rec.Metodo != "arduino" || rec.CliqueTelaMs == null)
            {
                rec.Data = DateTime.Now; rec.Metodo = "camera"; rec.CliqueTelaMs = Math.Round(ms, 1);
                _settings.LastLatency = rec;
                try { _settings.Save(); } catch { }
            }
        }
        else LatCamResult.Text = "";
    }

    private void BtnLatSaveSketch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pasta onde salvar o programa do Arduino" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var asm = typeof(MainWindow).Assembly;
            string? res = asm.GetManifestResourceNames().FirstOrDefault(n => n.Replace('\\', '/').EndsWith("ResetLatencia/ResetLatencia.ino", StringComparison.OrdinalIgnoreCase));
            using var src = (res != null ? asm.GetManifestResourceStream(res) : null) ?? throw new InvalidOperationException("programa não embutido neste .exe");
            // A Arduino IDE exige o .ino dentro de uma pasta com o mesmo nome.
            string dir = Path.Combine(dlg.FolderName, "ResetLatencia");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "ResetLatencia.ino");
            using (var dst = File.Create(path)) src.CopyTo(dst);
            LatSketchStatus.Text = $"Salvo em {path}. Abra na Arduino IDE e carregue na placa.";
            ShellRunner.OpenUrl(dir);
        }
        catch (Exception ex) { LatSketchStatus.Text = "Não foi possível salvar: " + ex.Message; }
    }
}
