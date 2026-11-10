namespace ResetExplode;

/// <summary>
/// Quanto o jogo demora para voltar ao ritmo normal depois de um alt-tab: do instante em que ele volta ao primeiro plano até o
/// primeiro segundo inteiro com pelo menos 90% do FPS de antes. shortcut: os sensores são de 1 s, então a resolução é de ~1 s;
/// serve para separar um alt-tab de 3 s de um de 0,5 s, não para medir milissegundos.
/// </summary>
public static class AltTabTimer
{
    /// <param name="events">(t_s, "segundo_plano" | "primeiro_plano") de eventos.csv.</param>
    /// <param name="fpsPerSecond">(t_s, fps) de sensores.csv; t_s é o fim do segundo.</param>
    /// <returns>ms de cada volta ao jogo. Volta sem recuperação até o fim da sessão usa o último segundo gravado.</returns>
    public static List<double> Measure(IReadOnlyList<(double T, string Kind)> events, IReadOnlyList<(double T, double Fps)> fpsPerSecond,
                                       double recoverFraction = 0.9)
    {
        var result = new List<double>();
        if (fpsPerSecond.Count == 0) return result;
        var sorted = events.OrderBy(e => e.T).ToList();
        double sessionMedian = Median(fpsPerSecond.Select(r => r.Fps).Where(f => f > 0));

        double? leftAt = null;
        foreach (var (t, kind) in sorted)
        {
            if (kind == "segundo_plano") { leftAt ??= t; continue; }
            if (kind != "primeiro_plano" || leftAt is not double t0) continue;
            leftAt = null;

            var before = fpsPerSecond.Where(r => r.T >= t0 - 6 && r.T <= t0 - 1 && r.Fps > 0).Select(r => r.Fps).ToList();
            double baseline = before.Count >= 2 ? Median(before) : sessionMedian;
            if (baseline <= 0) continue;

            double recoveredAt = fpsPerSecond[^1].T - 1;
            foreach (var row in fpsPerSecond)
                if (row.T - 1 >= t && row.Fps >= recoverFraction * baseline) { recoveredAt = row.T - 1; break; }
            result.Add(Math.Max(0, recoveredAt - t) * 1000);
        }
        return result;
    }

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        if (v.Count == 0) return 0;
        return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>Lê eventos.csv e sensores.csv da pasta e mede. Lista vazia se faltar arquivo.</summary>
    public static List<double> MeasureFolder(string folder)
    {
        try
        {
            var ev = CsvTable.Read(System.IO.Path.Combine(folder, "eventos.csv"));
            var sens = CsvTable.Read(System.IO.Path.Combine(folder, "sensores.csv"), c => c is "t_s" or "fps");
            if (ev == null || sens == null) return new List<double>();
            var events = ev.Rows.Where(r => ev.Get(r, "tipo") is "segundo_plano" or "primeiro_plano")
                .Select(r => (T: ev.Num(r, "t_s") ?? double.NaN, Kind: ev.Get(r, "tipo")!)).Where(e => !double.IsNaN(e.T)).ToList();
            var fps = sens.Rows.Select(r => (T: sens.Num(r, "t_s") ?? double.NaN, Fps: sens.Num(r, "fps") ?? 0)).Where(x => !double.IsNaN(x.T)).ToList();
            return Measure(events, fps);
        }
        catch { return new List<double>(); }
    }
}
