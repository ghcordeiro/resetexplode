using System.Globalization;
using System.Windows;
using LibreHardwareMonitor.Hardware;

namespace ResetExplode;

/// <summary>Teste de refrigeração (página Stress test): usa o gráfico, os cartões e o registro do stress test.</summary>
public partial class MainWindow
{
    private CoolingTest? _cool;

    private void BtnCooling_Click(object sender, RoutedEventArgs e)
    {
        if (_cool != null) { FinishCooling("Cancelado pelo usuário."); return; }
        if (TestRunning) { StressHint.Text = "Pare o stress test antes."; return; }
        if (!double.TryParse(TempLimit.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out _tempLimit) || _tempLimit < 40 || _tempLimit > 110)
        {
            StressHint.Text = "Limite da CPU deve estar entre 40 e 110 °C.";
            return;
        }
        if (_monitor.CpuTemperature == null)
        {
            StressHint.Text = "Sem sensor de temperatura da CPU (instale o PawnIO): o teste não tem o que medir.";
            return;
        }
        _cool = new CoolingTest();
        _tempHistory.Clear();
        _gpuTempHistory.Clear();
        BtnCooling.Content = "CANCELAR";
        BtnStress.IsEnabled = false;
        TempLimit.IsEnabled = false;
        SetStatus("Repouso", alert: false);
        StressHint.Text = "Não use o PC durante o teste (~7 min).";
        Log($"Teste de refrigeração · 1 min em repouso, 5 min de carga na CPU (para em {_tempLimit:0} °C), 1 min esfriando.");
    }

    /// <summary>Chamado a cada segundo pelo timer principal, depois de UpdateStressPanel.</summary>
    private void CoolingTick()
    {
        if (_cool == null || _monitor.CpuTemperature is not float temp) return;
        var now = DateTime.Now;
        _cool.Add(now, temp, _monitor.CpuPower, BoardFanRpm());
        double t = _cool.Elapsed(now);

        switch (_cool.Current)
        {
            case CoolingTest.Phase.Idle:
                StElapsed.Text = $"repouso {CoolingTest.IdleSeconds - t:0} s";
                if (t < CoolingTest.IdleSeconds) return;
                _cool.Next(now);
                _stressStart = now;
                _stressMaxTemp = 0;
                _stressDuration = TimeSpan.Zero; // a duração é controlada aqui
                _stress.Start(cpu: true, ram: false);
                SetStressControls(running: true);
                BtnStress.IsEnabled = false;
                SetStatus("Carga", alert: false);
                Log("Carga iniciada em todos os núcleos.");
                break;

            case CoolingTest.Phase.Load:
                // Bater o limite faz UpdateStressPanel parar o stress (corte de segurança); aqui só registramos.
                bool hit = !_stress.Running;
                if (!hit && t < CoolingTest.LoadSeconds) return;
                if (!hit) StopStress("Teste de refrigeração: fim da carga");
                BtnStress.IsEnabled = false;
                _cool.Next(now, hitLimit: hit);
                SetStatus("Esfriando", alert: false);
                break;

            case CoolingTest.Phase.Cooldown:
                StElapsed.Text = $"esfriando {CoolingTest.CooldownSeconds - t:0} s";
                if (t < CoolingTest.CooldownSeconds) return;
                var r = _cool.Analyze();
                FinishCooling(null);
                SetStatus(r.Verdict, alert: r.Verdict == "Ruim");
                Log($"Refrigeração: {r.Verdict.ToUpperInvariant()}");
                foreach (var line in r.Lines) Log("  " + line);
                AddEvent("", "Teste de refrigeração", r.Verdict, r.Verdict == "Bom" ? "ok" : "abortado");
                break;
        }
    }

    private void FinishCooling(string? reason)
    {
        if (_stress.Running) StopStress("Teste de refrigeração: " + (reason ?? "fim"));
        _cool = null;
        BtnCooling.Content = "TESTE DE REFRIGERAÇÃO";
        BtnStress.IsEnabled = true;
        TempLimit.IsEnabled = true;
        StressHint.Text = "";
        if (reason != null) { SetStatus("Parado", alert: false); Log(reason); }
    }

    /// <summary>Média de RPM das ventoinhas lidas pela placa-mãe (Super I/O), sem contar as da placa de vídeo.</summary>
    private float? BoardFanRpm()
    {
        var rpm = _monitor.Rows.Where(r => r.Type == SensorType.Fan && r.RawValue is > 0
                                        && r.HardwareType is HardwareType.Motherboard or HardwareType.SuperIO)
                               .Select(r => r.RawValue!.Value).ToList();
        return rpm.Count > 0 ? rpm.Average() : null;
    }
}
