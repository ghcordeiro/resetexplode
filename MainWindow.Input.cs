using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ResetExplode;

/// <summary>Sessões · MOUSE E TECLADO: testes avulsos de 5 s (taxa real de relatórios) e a opção de contar o teclado nas sessões.</summary>
public partial class MainWindow
{
    private RawInputMonitor.Tap? _inputTestTap;
    private const int InputTestSeconds = 5;

    private void BtnMouseTest_Click(object sender, RoutedEventArgs e) => RunInputTest(keyboard: false);
    private void BtnKeyTest_Click(object sender, RoutedEventArgs e) => RunInputTest(keyboard: true);

    private void RunInputTest(bool keyboard)
    {
        if (_inputTestTap != null) return;
        _inputTestTap = RawInputMonitor.Shared.Subscribe(keyboard);
        if (RawInputMonitor.Shared.Error is string err)
        {
            _inputTestTap.Dispose(); _inputTestTap = null;
            InputResult.Text = $"Não foi possível ler o {(keyboard ? "teclado" : "mouse")}: {err}";
            return;
        }
        BtnMouseTest.IsEnabled = BtnKeyTest.IsEnabled = false;
        // Teclas do teste não podem acionar botões nem digitar na tela.
        if (keyboard) PreviewKeyDown += SwallowKeys;
        int left = InputTestSeconds;
        string Prompt() => keyboard
            ? $"Aperte várias teclas ao mesmo tempo, rolando os dedos… {left} s"
            : $"Mexa o mouse em círculos rápidos, sem parar… {left} s";
        InputResult.Text = Prompt();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (--left > 0) { InputResult.Text = Prompt(); return; }
            timer.Stop();
            FinishInputTest(keyboard);
        };
        timer.Start();
    }

    private void FinishInputTest(bool keyboard)
    {
        var tap = _inputTestTap!;
        _inputTestTap = null;
        var times = keyboard ? tap.DrainKeyboard() : tap.DrainMouse();
        tap.Dispose();
        if (keyboard) PreviewKeyDown -= SwallowKeys;
        BtnMouseTest.IsEnabled = BtnKeyTest.IsEnabled = true;

        var last = _settings.LastInputTest ?? new SessionContext.TesteEntrada();
        last.Data = DateTime.Now;
        if (keyboard)
        {
            var r = InputAnalysis.KeyboardTest(times);
            if (r.Standard != null) { last.TecladoHz = r.Standard; last.TecladoVeredito = r.Verdict; }
            InputResult.Text = r.Verdict;
        }
        else
        {
            var r = InputAnalysis.MouseTest(times);
            if (r.Standard != null) { last.MouseHz = r.Standard; last.MouseVeredito = r.Verdict; }
            InputResult.Text = r.Verdict + (r.Hz != null ? $"  ({r.Reports} relatórios em {InputTestSeconds} s, pico de {r.MaxHz:0} Hz)" : "");
        }
        _settings.LastInputTest = last;
        try { _settings.Save(); } catch { }
        ShowLastInputTest();
    }

    private void SwallowKeys(object sender, KeyEventArgs e) => e.Handled = true;

    private void ShowLastInputTest()
    {
        var t = _settings.LastInputTest;
        InputLast.Text = t == null ? "Nenhum teste feito ainda. O resultado entra no contexto de cada sessão gravada."
            : $"Último teste ({t.Data:dd/MM HH:mm}): mouse {(t.MouseHz is double m ? $"{m:0} Hz" : "—")} · teclado {(t.TecladoHz is double k ? $"{k:0} Hz" : "—")}";
    }

    private void ChkSessKeyboard_Click(object sender, RoutedEventArgs e)
    {
        _settings.SessionKeyboard = ChkSessKeyboard.IsChecked == true;
        try { _settings.Save(); } catch { }
    }
}
