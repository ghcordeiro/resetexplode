using System.Globalization;

namespace ResetExplode;

public enum Verdict { Better, Worse, WithinNoise, NotEnoughRuns }

public sealed record MetricResult(string Name, double Median, double Min, double Max, int Runs)
{
    public double Range => Max - Min;
}

public sealed record Comparison(MetricResult Baseline, MetricResult Candidate, double DiffPct, Verdict Verdict, bool HigherIsBetter)
{
    public string VerdictText => Verdict switch
    {
        Verdict.Better => "MELHOR", Verdict.Worse => "PIOR", Verdict.WithinNoise => "dentro do ruído", _ => "poucas execuções (mínimo 3)"
    };

    public string Line
    {
        get
        {
            static string R(MetricResult m) => string.Create(CultureInfo.CurrentCulture, $"{m.Median:0.#} [{m.Min:0.#}–{m.Max:0.#}]");
            string pct = string.Create(CultureInfo.CurrentCulture, $"{(DiffPct > 0 ? "+" : "")}{DiffPct:0.#}%");
            return $"{Baseline.Name}: {R(Baseline)} → {R(Candidate)}  {pct}  {VerdictText}";
        }
    }
}

/// <summary>
/// Compara dois perfis de teste. Uma configuração só é melhor que a outra quando a diferença das medianas é maior que a maior
/// variação (mín–máx) entre execuções repetidas de qualquer um dos dois: o ruído do próprio teste.
/// </summary>
public static class ProfileComparer
{
    public const int MinRuns = 3;

    public static MetricResult Summarize(string name, IEnumerable<double> values)
    {
        var v = values.Where(double.IsFinite).OrderBy(x => x).ToList();
        if (v.Count == 0) return new MetricResult(name, 0, 0, 0, 0);
        double median = v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        return new MetricResult(name, median, v[0], v[^1], v.Count);
    }

    public static Comparison Compare(string name, IReadOnlyList<double> baseline, IReadOnlyList<double> candidate, bool higherIsBetter)
    {
        var b = Summarize(name, baseline);
        var c = Summarize(name, candidate);
        double diff = c.Median - b.Median;
        double pct = b.Median != 0 ? 100 * diff / Math.Abs(b.Median) : 0;
        Verdict verdict;
        if (b.Runs < MinRuns || c.Runs < MinRuns) verdict = Verdict.NotEnoughRuns;
        else if (Math.Abs(diff) <= Math.Max(b.Range, c.Range)) verdict = Verdict.WithinNoise;
        else verdict = (diff > 0) == higherIsBetter ? Verdict.Better : Verdict.Worse;
        return new Comparison(b, c, pct, verdict, higherIsBetter);
    }

    /// <summary>As métricas principais de dois perfis, só com execuções válidas e fora do aquecimento.</summary>
    public static List<Comparison> CompareRuns(IEnumerable<RunRecord> baseline, IEnumerable<RunRecord> candidate)
    {
        var a = baseline.Where(r => r.Valid && !r.WarmUp).ToList();
        var b = candidate.Where(r => r.Valid && !r.WarmUp).ToList();
        List<double> P(List<RunRecord> l, Func<RunRecord, double> f) => l.Select(f).ToList();
        var rows = new List<Comparison>
        {
            Compare("1% low (fps)", P(a, r => r.Low1Fps), P(b, r => r.Low1Fps), true),
            Compare("0,1% low (fps)", P(a, r => r.Low01Fps), P(b, r => r.Low01Fps), true),
            Compare("Fps médio", P(a, r => r.AvgFps), P(b, r => r.AvgFps), true),
            Compare("Desvio do frametime (ms)", P(a, r => r.StdDevMs), P(b, r => r.StdDevMs), false),
            Compare("Quedas", P(a, r => r.Drops), P(b, r => r.Drops), false),
        };
        // Latência: só entra quando os dois perfis têm o dado (o PresentMon pode não medir, ex.: sem Reflex não há "latência do PC").
        void Lat(string name, Func<RunRecord, double> f)
        {
            var la = a.Select(f).Where(x => x > 0).ToList(); var lb = b.Select(f).Where(x => x > 0).ToList();
            if (la.Count > 0 && lb.Count > 0) rows.Add(Compare(name, la, lb, false));
        }
        Lat("Latência até a tela (ms)", r => r.DisplayLatMs);
        Lat("Latência do PC (ms)", r => r.PcLatMs);
        Lat("Clique até a imagem (ms)", r => r.ClickLatMs);
        // Alt-tab só entra se as execuções tiveram alt-tab medido (a captura válida normalmente não tem: serve às gravações do dia a dia).
        var altA = a.Where(r => r.AltTabMs > 0).Select(r => r.AltTabMs).ToList();
        var altB = b.Where(r => r.AltTabMs > 0).Select(r => r.AltTabMs).ToList();
        if (altA.Count > 0 && altB.Count > 0) rows.Add(Compare("Alt-tab (ms)", altA, altB, false));
        return rows;
    }
}
