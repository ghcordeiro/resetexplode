namespace ResetExplode;

/// <summary>
/// Contas sobre os instantes (em ms) em que relatórios do mouse e do teclado chegaram ao PC.
/// Sem dependências do Windows: testável com tempos sintéticos.
/// </summary>
public static class InputAnalysis
{
    public static readonly int[] StandardRates = { 125, 250, 500, 1000, 2000, 4000, 8000 };

    /// <summary>Intervalos entre relatórios seguidos, só dentro de "rajadas" de movimento (pausas maiores que 50 ms ficam de fora).</summary>
    public static List<double> BurstIntervals(IReadOnlyList<double> timesMs, double maxGapMs = 50)
    {
        var list = new List<double>(Math.Max(0, timesMs.Count - 1));
        for (int i = 1; i < timesMs.Count; i++)
        {
            double d = timesMs[i] - timesMs[i - 1];
            if (d > 0 && d <= maxGapMs) list.Add(d);
        }
        return list;
    }

    public static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(sorted.Count * p))];

    /// <summary>
    /// Relatórios contados em janelas de 100 ms dentro de rajadas de movimento (pausa maior que 50 ms encerra a rajada).
    /// Contar por janela, e não intervalo a intervalo, ignora o Windows entregando relatórios em pequenos lotes;
    /// falhas reais (sem fio com interferência, porta/cabo ruim) derrubam a contagem da janela.
    /// </summary>
    public static List<int> WindowCounts(IReadOnlyList<double> t, double windowMs = 100, double maxGapMs = 50)
    {
        var counts = new List<int>();
        int i = 0;
        while (i < t.Count)
        {
            int j = i;
            while (j + 1 < t.Count && t[j + 1] - t[j] <= maxGapMs) j++;
            int k = i;
            for (double w = t[i]; w + windowMs <= t[j]; w += windowMs)
            {
                int c = 0;
                while (k <= j && t[k] < w + windowMs) { if (t[k] >= w) c++; k++; }
                counts.Add(c);
            }
            i = j + 1;
        }
        return counts;
    }

    /// <summary>
    /// Taxa do mouse (Hz) pela mediana das janelas de 100 ms e a irregularidade de chegada (p90 − p10 dos intervalos, em ms;
    /// inclui o lote do Windows, então serve para comparar sessões, não para julgar o mouse). Exige ≥ 300 ms de movimento.
    /// </summary>
    public static (double? Hz, double? JitterMs) MouseRate(IReadOnlyList<double> timesMs, int minWindows = 3)
    {
        var w = WindowCounts(timesMs);
        if (w.Count < minWindows) return (null, null);
        w.Sort();
        var iv = BurstIntervals(timesMs);
        iv.Sort();
        return (w[w.Count / 2] * 10.0, iv.Count > 0 ? Percentile(iv, 0.9) - Percentile(iv, 0.1) : null);
    }

    public static int NearestStandard(double hz) => StandardRates.OrderBy(r => Math.Abs(Math.Log(hz / r))).First();

    public sealed record MouseTestResult(int Reports, double? Hz, int? Standard, double StablePct, double? MaxHz, string Verdict);

    /// <summary>Teste avulso: o usuário mexe o mouse em círculos por alguns segundos.</summary>
    public static MouseTestResult MouseTest(IReadOnlyList<double> timesMs)
    {
        var w = WindowCounts(timesMs);
        if (w.Count < 10)
            return new(timesMs.Count, null, null, 0, null,
                "Pouco movimento para medir. Clique em TESTAR MOUSE e mexa o mouse em círculos rápidos sem parar durante os 5 s.");
        var sorted = w.OrderBy(x => x).ToList();
        double med = sorted[sorted.Count / 2], hz = med * 10;
        int std = NearestStandard(hz);
        double stable = 100.0 * w.Count(c => Math.Abs(c - med) <= med * 0.15) / w.Count;
        double maxHz = sorted[^1] * 10;
        string verdict = stable < 70
            ? $"Mouse a ~{hz:0} Hz, mas irregular: em {100 - stable:0}% dos trechos de 0,1 s chegaram bem menos relatórios. Ligue direto numa porta USB " +
              "traseira da placa-mãe (sem hub/extensor) e ative \"Sem economia de energia em USB\" em RESET OTIMIZA. Mouse sem fio: aproxime o receptor."
            : std <= 500
                ? $"Mouse a {std} Hz ({hz:0} Hz medido), estável. Se o seu mouse suporta 1000 Hz ou mais, aumente a taxa (polling rate) no software do fabricante: cada relatório chega até {1000.0 / std - 1:0} ms antes."
                : $"Mouse a {std} Hz ({hz:0} Hz medido), estável ({stable:0}% dos trechos no ritmo). Ótimo para jogos.";
        return new(timesMs.Count, Math.Round(hz), std, Math.Round(stable), Math.Round(maxHz), verdict);
    }

    public sealed record KeyboardTestResult(int Events, int CloseGaps, double? Hz, int? Standard, string Verdict);

    /// <summary>
    /// Teste avulso do teclado: o usuário aperta várias teclas ao mesmo tempo, rolando os dedos. Eventos que chegam no mesmo
    /// relatório USB ficam a menos de 0,3 ms um do outro; relatórios diferentes ficam separados por múltiplos do intervalo de
    /// polling. O menor intervalo que se repete (percentil 5 dos intervalos curtos) é o período do polling.
    /// </summary>
    public static KeyboardTestResult KeyboardTest(IReadOnlyList<double> timesMs)
    {
        var gaps = new List<double>();
        for (int i = 1; i < timesMs.Count; i++)
        {
            double d = timesMs[i] - timesMs[i - 1];
            if (d > 0.3 && d < 20) gaps.Add(d);
        }
        if (gaps.Count < 15)
            return new(timesMs.Count, gaps.Count, null, null,
                "Poucas teclas juntas para medir. Clique em TESTAR TECLADO e aperte várias teclas ao mesmo tempo, rolando os dedos (como um \"tamborilar\"), durante os 5 s.");
        gaps.Sort();
        double period = Percentile(gaps, 0.05), hz = 1000 / period;
        int std = NearestStandard(hz);
        string verdict = std <= 125
            ? $"Teclado a ~{std} Hz (um relatório a cada ~{1000.0 / std:0} ms). É o padrão de teclados comuns; teclados para jogos chegam a 1000 Hz (1 ms). Estimativa: repita o teste para confirmar."
            : $"Teclado a ~{std} Hz (um relatório a cada ~{1000.0 / std:0.#} ms). Estimativa: repita o teste para confirmar.";
        return new(timesMs.Count, gaps.Count, Math.Round(hz), std, verdict);
    }
}
