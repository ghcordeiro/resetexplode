using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace ResetExplode;

/// <summary>Relatório HTML único (sem rede) comparando dois perfis de teste: tabela com veredito, frametime no tempo, histograma, diff e sugestões.</summary>
public static class ProfileReport
{
    private const int MaxPoints = 1500;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(string baselineName, string candidateName, IReadOnlyList<Comparison> rows,
                               IReadOnlyList<float> baselineFrames, IReadOnlyList<float> candidateFrames,
                               IReadOnlyList<string> configDiff, IReadOnlyList<Suggestion> suggestions)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<title>Comparação de perfis · CS2</title><style>")
          .Append(":root{--bg:#fff;--fg:#111;--mut:#666;--line:#ddd;--a:#8a8a8a;--b:#e4572e;--ok:#1a7f37;--bad:#c62828}")
          .Append("@media(prefers-color-scheme:dark){:root{--bg:#111;--fg:#eee;--mut:#999;--line:#333;--a:#9a9a9a;--b:#ff7a50;--ok:#4cc16b;--bad:#ff6b6b}}")
          .Append("body{background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,sans-serif;margin:0;padding:24px 16px}main{max-width:900px;margin:auto}")
          .Append("h1{font-size:24px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 8px;text-transform:uppercase;letter-spacing:.05em;color:var(--mut)}")
          .Append("table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 8px;border-bottom:1px solid var(--line);font-variant-numeric:tabular-nums}")
          .Append(".b{color:var(--ok);font-weight:600}.p{color:var(--bad);font-weight:600}.n{color:var(--mut)}svg{width:100%;height:auto;display:block}")
          .Append(".key span{display:inline-block;width:12px;height:12px;margin:0 6px 0 14px;vertical-align:-1px}li{margin:4px 0}.mut{color:var(--mut)}")
          .Append("</style></head><body><main>");
        sb.Append("<h1>").Append(E(baselineName)).Append(" → ").Append(E(candidateName)).Append("</h1>")
          .Append("<div class=\"mut\">Gerado em ").Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm", Inv)).Append("</div>");

        sb.Append("<h2>Resultado</h2><table><tr><th>Métrica</th><th>").Append(E(baselineName)).Append("</th><th>").Append(E(candidateName))
          .Append("</th><th>Diferença</th><th>Veredito</th></tr>");
        foreach (var c in rows)
        {
            string cls = c.Verdict switch { Verdict.Better => "b", Verdict.Worse => "p", _ => "n" };
            sb.Append("<tr><td>").Append(E(c.Baseline.Name)).Append("</td><td>").Append(Cell(c.Baseline)).Append("</td><td>").Append(Cell(c.Candidate))
              .Append("</td><td>").Append(c.DiffPct.ToString("+0.#;-0.#;0", Inv)).Append("%</td><td class=\"").Append(cls).Append("\">")
              .Append(E(c.VerdictText)).Append("</td></tr>");
        }
        sb.Append("</table><div class=\"mut\">Mediana [mínimo–máximo] das execuções válidas. \"Melhor\" ou \"pior\" só quando a diferença das medianas supera a variação entre execuções repetidas.</div>");

        sb.Append("<h2>Frametime ao longo do tempo</h2><div class=\"key\"><span style=\"background:var(--a)\"></span>").Append(E(baselineName))
          .Append("<span style=\"background:var(--b)\"></span>").Append(E(candidateName)).Append("</div>").Append(TimeSeries(baselineFrames, candidateFrames));
        sb.Append("<h2>Distribuição do frametime</h2>").Append(Histogram(baselineFrames, candidateFrames));

        sb.Append("<h2>O que mudou no PC</h2>");
        if (configDiff.Count == 0) sb.Append("<div class=\"mut\">Nenhuma mudança de configuração detectada.</div>");
        else { sb.Append("<ul>"); foreach (var d in configDiff) sb.Append("<li>").Append(E(d)).Append("</li>"); sb.Append("</ul>"); }

        if (suggestions.Count > 0)
        {
            sb.Append("<h2>Sugestões para testar</h2><ul>");
            foreach (var s in suggestions) sb.Append("<li>").Append(E(s.Text)).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("</main></body></html>");
        return sb.ToString();

        static string Cell(MetricResult m) => m.Runs == 0 ? "—" : $"{m.Median.ToString("0.#", Inv)} <span class=\"n\">[{m.Min.ToString("0.#", Inv)}–{m.Max.ToString("0.#", Inv)}] ({m.Runs})</span>";
    }

    /// <summary>Linha por bloco, guardando o MÁXIMO do bloco (o que importa são os picos, que uma média esconderia).</summary>
    private static List<double> Reduce(IReadOnlyList<float> frames)
    {
        var list = new List<double>();
        if (frames.Count == 0) return list;
        int block = Math.Max(1, (int)Math.Ceiling(frames.Count / (double)MaxPoints));
        for (int i = 0; i < frames.Count; i += block)
        {
            double max = 0;
            for (int k = i; k < Math.Min(frames.Count, i + block); k++) max = Math.Max(max, frames[k]);
            list.Add(max);
        }
        return list;
    }

    private static string TimeSeries(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        var sa = Reduce(a); var sb2 = Reduce(b);
        double top = Math.Max(1, new[] { sa.DefaultIfEmpty(0).Max(), sb2.DefaultIfEmpty(0).Max() }.Max());
        top = Math.Min(top, Math.Max(20, Percentile(a, 0.999) * 2.5)); // pico gigante não achata o resto
        const int W = 900, H = 260, L = 44, B = 24;
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Frametime ao longo do tempo\">");
        for (int i = 0; i <= 4; i++)
        {
            double y = (H - B) - (H - B - 8) * i / 4.0;
            sb.Append($"<line x1=\"{L}\" x2=\"{W - 6}\" y1=\"{y.ToString("0.#", Inv)}\" y2=\"{y.ToString("0.#", Inv)}\" stroke=\"var(--line)\"/>")
              .Append($"<text x=\"{L - 6}\" y=\"{(y + 4).ToString("0.#", Inv)}\" text-anchor=\"end\" font-size=\"11\" fill=\"var(--mut)\">{(top * i / 4).ToString("0.#", Inv)}</text>");
        }
        sb.Append($"<text x=\"{L}\" y=\"{H - 6}\" font-size=\"11\" fill=\"var(--mut)\">ms por quadro (cada ponto é o pior quadro do trecho)</text>");
        Line(sb, sa, top, W, H, L, B, "var(--a)");
        Line(sb, sb2, top, W, H, L, B, "var(--b)");
        return sb.Append("</svg>").ToString();
    }

    private static void Line(StringBuilder sb, List<double> s, double top, int W, int H, int L, int B, string color)
    {
        if (s.Count < 2) return;
        sb.Append("<polyline fill=\"none\" stroke-width=\"1\" stroke=\"").Append(color).Append("\" points=\"");
        for (int i = 0; i < s.Count; i++)
        {
            double x = L + (W - L - 6) * i / (double)(s.Count - 1);
            double y = (H - B) - (H - B - 8) * Math.Min(s[i], top) / top;
            sb.Append(x.ToString("0.#", Inv)).Append(',').Append(y.ToString("0.#", Inv)).Append(' ');
        }
        sb.Append("\"/>");
    }

    private static string Histogram(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        const int Bins = 40, W = 900, H = 220, L = 44, B = 24;
        double max = Math.Max(Percentile(a, 0.999), Percentile(b, 0.999)) * 1.1;
        if (max <= 0) return "<div class=\"mut\">Sem quadros para mostrar.</div>";
        double[] Bin(IReadOnlyList<float> f)
        {
            var h = new double[Bins];
            foreach (var v in f) h[Math.Min(Bins - 1, (int)(v / max * Bins))]++;
            if (f.Count > 0) for (int i = 0; i < Bins; i++) h[i] = 100.0 * h[i] / f.Count;
            return h;
        }
        var ha = Bin(a); var hb = Bin(b);
        double top = Math.Max(1, Math.Max(ha.Max(), hb.Max()));
        var sb = new StringBuilder($"<svg viewBox=\"0 0 {W} {H}\" role=\"img\" aria-label=\"Histograma de frametime\">");
        double bw = (W - L - 6) / (double)Bins;
        for (int i = 0; i < Bins; i++)
        {
            foreach (var (h, color, off) in new[] { (ha, "var(--a)", 0.0), (hb, "var(--b)", 0.5) })
            {
                double hh = (H - B - 8) * h[i] / top;
                sb.Append($"<rect x=\"{(L + i * bw + off * bw).ToString("0.#", Inv)}\" y=\"{(H - B - hh).ToString("0.#", Inv)}\" width=\"{(bw * 0.5).ToString("0.#", Inv)}\" height=\"{hh.ToString("0.#", Inv)}\" fill=\"{color}\"/>");
            }
        }
        sb.Append($"<text x=\"{L}\" y=\"{H - 6}\" font-size=\"11\" fill=\"var(--mut)\">0 ms</text>")
          .Append($"<text x=\"{W - 6}\" y=\"{H - 6}\" font-size=\"11\" text-anchor=\"end\" fill=\"var(--mut)\">{max.ToString("0.#", Inv)} ms · % dos quadros</text></svg>");
        return sb.ToString();
    }

    private static double Percentile(IReadOnlyList<float> f, double p)
    {
        if (f.Count == 0) return 0;
        var s = f.ToArray();
        Array.Sort(s);
        return s[(int)Math.Min(s.Length - 1, Math.Floor(s.Length * p))];
    }

    /// <summary>frames.csv (instante, frametime ms) → só os frametimes. Arquivo ausente ou ruim devolve lista vazia.</summary>
    public static List<float> ReadFrames(string sessionFolder)
    {
        var list = new List<float>();
        try
        {
            string path = Path.Combine(sessionFolder, "frames.csv");
            if (!File.Exists(path)) return list;
            using var r = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            r.ReadLine();
            for (string? line = r.ReadLine(); line != null; line = r.ReadLine())
            {
                int c = line.IndexOf(',');
                if (c > 0 && float.TryParse(line.AsSpan(c + 1), NumberStyles.Float, Inv, out float ft) && ft > 0) list.Add(ft);
            }
        }
        catch { }
        return list;
    }
}
