using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>
/// Laudo de uma sessão de jogo, sem IA: lê os arquivos da pasta (resumo, contexto, sensores, eventos, processos, exibição),
/// liga cada engasgo ao que os sensores mostravam naquele segundo e junta tudo em linhas com gravidade e conselho.
/// Grava "laudo" no resumo.json e laudo.txt. <see cref="Compare"/> diz o que mudou entre duas sessões do mesmo jogo.
/// </summary>
public static class SessionAnalyzer
{
    public sealed record Linha(AvisoNivel? Nivel, string Texto)
    {
        public override string ToString() => (Nivel switch
        {
            AvisoNivel.Problema => "PROBLEMA", AvisoNivel.Atencao => "ATENÇÃO", AvisoNivel.Dica => "DICA", _ => "OK"
        }) + ": " + Texto;
    }

    public sealed record Laudo(string Veredito, List<Linha> Linhas, Dictionary<string, int> CausasEngasgos);

    /// <summary>Analisa e grava o laudo na pasta (resumo.json + laudo.txt). Devolve null se a sessão não tem resumo.</summary>
    public static Laudo? Run(string folder)
    {
        string resumoPath = Path.Combine(folder, "resumo.json");
        if (!File.Exists(resumoPath)) return null;
        var resumo = JsonNode.Parse(File.ReadAllText(resumoPath))!.AsObject();
        var alt = AltTabTimer.MeasureFolder(folder);
        if (alt.Count > 0)
        {
            resumo["alttab_ms"] = new JsonArray(alt.Select(x => (JsonNode?)JsonValue.Create(Math.Round(x))).ToArray());
            resumo["alttab_mediana_ms"] = Math.Round(AltTabTimer.Median(alt));
        }
        var laudo = Analyze(folder, resumo);
        resumo["laudo"] = new JsonObject
        {
            ["veredito"] = laudo.Veredito,
            ["linhas"] = new JsonArray(laudo.Linhas.Select(l => (JsonNode?)JsonValue.Create(l.ToString())).ToArray()),
            ["causas_engasgos"] = new JsonObject(laudo.CausasEngasgos.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
        };
        var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(resumoPath, resumo.ToJsonString(opts), new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(folder, "laudo.txt"),
            new[] { $"LAUDO: {laudo.Veredito.ToUpperInvariant()}", "" }.Concat(laudo.Linhas.Select(l => "- " + l)), new UTF8Encoding(false));
        return laudo;
    }

    public static Laudo Analyze(string folder, JsonObject r)
    {
        var lines = new List<Linha>();
        var ctx = SessionContext.Load(folder);
        double dur = D(r, "duracao_s") ?? 0, minutes = Math.Max(1 / 60.0, dur / 60);
        double fps = D(r, "fps_medio") ?? 0, low1 = D(r, "low_1pct_fps") ?? 0, low01 = D(r, "low_01pct_fps") ?? 0;
        int stutters = (int)(D(r, "engasgos") ?? 0);

        // 1) Desempenho e estabilidade
        if (fps > 0)
        {
            double ratio = low1 / fps;
            string s = $"{fps:0} FPS em média, 1% low {low1:0}, 0,1% low {low01:0} em {TimeSpan.FromSeconds(dur):h\\:mm\\:ss}";
            double ratio01 = low01 / fps;
            lines.Add(ratio >= 0.7 && ratio01 >= 0.4 ? new(null, s + ": estável.")
                : ratio >= 0.7 ? new(AvisoNivel.Atencao, s + ": estável na maior parte, mas o 0,1% low mostra travadas raras e fortes.")
                : ratio >= 0.5 ? new(AvisoNivel.Atencao, s + $": o 1% low é {ratio:P0} da média, a variação é perceptível.")
                : new(AvisoNivel.Problema, s + $": instável, o 1% low é só {ratio:P0} da média (travadinhas visíveis)."));
        }

        // 2) Engasgos e a causa de cada um
        var causes = AttributeStutters(folder, ctx, out var topBackground);
        double perMin = stutters / minutes;
        if (stutters > 0)
        {
            var parts = causes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {CauseLabel(kv.Key, topBackground)}");
            var nivel = perMin >= 6 ? AvisoNivel.Problema : perMin >= 1 ? AvisoNivel.Atencao : AvisoNivel.Dica;
            lines.Add(new(nivel, $"{stutters} engasgos ({perMin:0.#} por minuto): " + string.Join(", ", parts) + "."));
            foreach (var cause in causes.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key))
                if (CauseAdvice(cause, ctx, topBackground) is string advice) lines.Add(new(AvisoNivel.Dica, advice));
        }
        else if (fps > 0) lines.Add(new(null, "Nenhum engasgo (frame 3× mais lento que os vizinhos) durante a sessão."));

        // 3) Gargalo (PresentMon por frame, ou os sensores)
        var exib = r["exibicao"] as JsonObject;
        var bound = (exib?["gargalo_pct_do_tempo"] as JsonObject)?.ToDictionary(kv => kv.Key, kv => (double?)kv.Value ?? 0);
        int? hz = ctx?.Monitor?.HzAtual;
        if (bound is { Count: > 0 })
        {
            var main = bound.OrderByDescending(kv => kv.Value).First();
            string where = main.Key switch { "GPU" => "a placa de vídeo", "CPU" => "o processador", _ => "uma trava de FPS/V-Sync (nem CPU nem GPU no limite)" };
            lines.Add(new(null, $"Gargalo: {where} em {main.Value:0}% do tempo (medido frame a frame pelo PresentMon)."));
            if (main.Key == "CPU" && hz is int h1 && fps < h1 * 0.9)
                lines.Add(new(AvisoNivel.Dica, BottleneckAnalyzer.Advice(Limiter.Cpu)));
            if (main.Key == "GPU" && hz is int h2 && fps < h2 * 0.9)
                lines.Add(new(AvisoNivel.Dica, $"A placa de vídeo é o limite e o FPS ({fps:0}) ficou abaixo da taxa do monitor ({h2} Hz): " +
                                               "reduza resolução/qualidade ou ative DLSS/FSR para chegar perto dela."));
        }
        else if (r["limitador_pct_do_tempo"] is JsonObject lim && lim.Count > 0)
        {
            var main = lim.Select(kv => (kv.Key, V: (double?)kv.Value ?? 0)).OrderByDescending(x => x.V).First();
            if (Enum.TryParse<Limiter>(main.Key, out var l))
                lines.Add(new(l is Limiter.Gpu or Limiter.Capped ? null : AvisoNivel.Atencao,
                    $"{BottleneckAnalyzer.Title(l)} em {main.V:0}% do tempo. {BottleneckAnalyzer.Advice(l)}"));
        }

        // 4) Latência e apresentação (PresentMon)
        if (exib != null && exib["erro"] == null)
        {
            double? lat = D(exib["latencia_exibicao_ms"] as JsonObject, "media"), ft = D(exib, "frametime_medio_ms");
            var modes = (exib["modo_apresentacao_pct"] as JsonObject)?.Select(kv => (kv.Key, V: (double?)kv.Value ?? 0)).OrderByDescending(x => x.V).ToList();
            string? mode = modes?.FirstOrDefault().Key;
            if (lat is double la && ft is double f && f > 0)
            {
                string s = $"Latência do frame até a tela: {la:0.#} ms em média (frame de {f:0.#} ms)";
                bool queued = la > f * 2.5 && la > 12;
                lines.Add(queued
                    ? new(AvisoNivel.Atencao, s + ": há fila de frames esperando a tela. Ative NVIDIA Reflex ou AMD Anti-Lag no jogo, " +
                                               (hz is int h3 ? $"ou limite o FPS a ~{Math.Max(30, h3 - 3)} " : "ou limite o FPS ") + "(fica dentro do G-Sync/FreeSync).")
                    : new(null, s + "."));
            }
            if (mode != null && mode.StartsWith("Composed", StringComparison.OrdinalIgnoreCase))
                lines.Add(new(AvisoNivel.Atencao,
                    $"O jogo passou pelo compositor do Windows (\"{mode}\"): +1 frame de atraso. Use tela cheia exclusiva, ou ligue " +
                    "\"Otimizações para jogos em janela\" no RESET OTIMIZA, e feche overlays que ficam por cima do jogo."));
            if (D(exib, "frames_gerados_pct") is double gen && gen > 1)
                lines.Add(new(AvisoNivel.Dica, $"Geração de quadros ativa ({gen:0}% dos frames gerados): o FPS exibido sobe, mas a latência segue a dos frames renderizados."));
            if (D(exib, "descartados_pct") is double drop && drop > 5)
                lines.Add(new(AvisoNivel.Atencao, $"{drop:0}% dos frames não chegaram à tela (descartados): com FPS acima da taxa do monitor e sem tearing, limite o FPS."));
            if (D(exib, "latencia_clique_ms_media") is double click && (int)(D(exib, "latencia_clique_amostras") ?? 0) >= 5)
                lines.Add(new(null, $"Clique até a imagem (sem contar mouse e monitor): {click:0.#} ms em média."));
            if (hz is int h4 && D(exib, "fps_exibido_medio") is double fe && fe > h4 * 1.15 && (D(exib, "tearing_permitido_pct") ?? 0) > 50)
                lines.Add(new(AvisoNivel.Dica, $"FPS ({fe:0}) bem acima da taxa do monitor ({h4} Hz) e com tearing: limitar a ~{h4 - 3} FPS reduz calor e consumo; com G-Sync/FreeSync, também a latência."));
        }

        // 4a) Causa das quedas frame a frame (PresentMon)
        if (exib?["quedas_causa_pct"] is JsonObject causes2 && causes2.Count > 0)
        {
            string Pct(string k) => D(causes2, k) is double v ? $"{v:0}%" : "0%";
            lines.Add(new(null, $"Quedas medidas frame a frame ({D(exib, "quedas"):0}): CPU ocupada em {Pct("CPU")}, GPU em {Pct("GPU")} e " +
                                $"espera do sistema em {Pct("Sistema")} (nem CPU nem GPU trabalhando: driver, interrupção, paginação ou sincronia)."));
        }

        // 4a1) Onde as quedas aconteceram (eventos da cena, ex.: explosão)
        if (r["quedas_eventos"] is JsonArray qev && qev.Count > 0)
        {
            var top = qev.OfType<JsonObject>().OrderByDescending(e => D(e, "pior_ms") ?? 0).Take(5).OrderBy(e => D(e, "t_s") ?? 0).ToList();
            lines.Add(new(null, "Maiores quedas (instante na janela · quadro · pior frame): " + string.Join("; ", top.Select(e =>
                $"{D(e, "t_s"):0.#} s · quadro {D(e, "quadro"):0} · {D(e, "pior_ms"):0.#} ms ({D(e, "fps_min"):0} fps)")) +
                ". Quedas que se repetem no mesmo instante em todas as execuções são eventos da cena (explosão, fumaça), não travadas aleatórias."));
        }

        // 4a2) Tabela do mapa de benchmark (VProf): onde o frame gasta tempo
        if (r["vprof"] is JsonObject vp && vp["linhas"] is JsonArray vrows)
        {
            double? Av(string name, string field) => vrows.OfType<JsonObject>().FirstOrDefault(x => x["nome"]?.ToString() == name) is { } o ? D(o, field) : null;
            lines.Add(new(null, $"Mapa: FPS médio {D(vp, "avg"):0.#}, P1 {D(vp, "p1"):0.#}, janela de {D(vp, "intervalos"):0} s com {D(vp, "frames"):0} frames " +
                                $"({D(vp, "excluidos"):0} frames excluídos pelo mapa). O 1% low desta captura usa só essa janela."));
            if (Av("Server Simulation", "ativos_n") is double tickN && D(vp, "frames") is double tf && tf > 0 && Av("Server Simulation", "ativos_avg_ms") is double tickMs)
                lines.Add(new(null, $"O mapa roda um servidor local: {tickN / tf * 100:0}% dos frames são frames de tick e custam ~{tickMs:0.#} ms a mais (Server Simulation). " +
                                    "Numa partida online esse custo não existe: o 1% low do mapa é mais pessimista que o do jogo. Vale para comparar perfis, não como número absoluto."));
            if (Av("FrameTotal", "todos_p99_ms") is double p99 && Av("Client Rendering", "todos_avg_ms") is double rend && Av("FrameTotal", "todos_avg_ms") is double tot)
                lines.Add(new(null, $"Frame médio de {tot:0.##} ms (P99 {p99:0.##} ms). Renderização do cliente: {rend:0.##} ms ({rend / tot * 100:0}%); " +
                                    $"pior frame de cada segundo: mediana {Av("FrameTotal", "max1s_p50_ms"):0.#} ms, P95 {Av("FrameTotal", "max1s_p95_ms"):0.#} ms."));
            if (Av("LowLatencySleep", "todos_avg_ms") is double sleepMs && sleepMs >= 0.1)
                lines.Add(new(null, $"Reflex ativo: o jogo dorme {sleepMs:0.##} ms por frame em média (até {Av("LowLatencySleep", "todos_p99_ms"):0.#} ms) para manter a fila da GPU vazia."));
        }

        // 4b) Alt-tab: tempo para o jogo voltar ao ritmo normal
        if (r["alttab_ms"] is JsonArray alts && alts.Count > 0 && D(r, "alttab_mediana_ms") is double altMed)
            lines.Add(altMed >= 2000
                ? new(AvisoNivel.Atencao, $"Alt-tab lento: o jogo levou {altMed / 1000:0.#} s (mediana de {alts.Count} voltas) para recuperar o FPS. " +
                                          "Em tela cheia exclusiva, cada alt-tab troca o modo do monitor; teste janela sem borda com a área de trabalho no mesmo tamanho (aba Biblioteca).")
                : new(null, $"Alt-tab: {altMed / 1000:0.#} s para o jogo recuperar o FPS ({alts.Count} voltas, resolução de 1 s)."));

        // 4c) Diagnóstico profundo (DPC/ISR)
        if (r["dpc_isr"] is JsonObject dpc)
        {
            if (dpc["erro"] != null) lines.Add(new(AvisoNivel.Dica, "Diagnóstico profundo não rodou: " + dpc["erro"]));
            else if (dpc["drivers"] is JsonArray drv && drv.Count > 0)
                lines.Add(new(AvisoNivel.Atencao, $"{D(dpc, "picos"):0} atrasos de DPC acima de {D(dpc, "limite_ms"):0.#} ms. Drivers que mais atrasaram: " +
                    string.Join("; ", drv.OfType<JsonObject>().Take(3).Select(d => $"{d["driver"]} ({D(d, "picos"):0} picos, até {D(d, "maximo_us"):0} µs)")) +
                    ". Atualize ou desative o dispositivo do primeiro (placa de rede, áudio e USB são os suspeitos comuns); os instantes estão em dpc.csv."));
            else lines.Add(new(null, "Diagnóstico profundo: nenhum DPC/ISR passou de 0,5 ms."));
        }

        if (r["thread_principal"] is JsonObject tp)
            lines.Add(new(D(tp, "segundos_acima_90_pct") >= 20 ? AvisoNivel.Atencao : null,
                $"Thread principal do CS2: {D(tp, "media_pct"):0}% de um núcleo em média, acima de 90% em {D(tp, "segundos_acima_90_pct"):0}% dos segundos" +
                (D(tp, "segundos_acima_90_pct") >= 20 ? ": a CPU é o limite (um núcleo mais rápido ou com mais cache ajudaria)." : ".")));

        // 5) Rede
        if (r["rede"] is JsonObject net)
        {
            var gw = net["roteador"] as JsonObject; var it = net["internet_1111"] as JsonObject;
            double gwLoss = D(gw, "perda_pct") ?? 0, itLoss = D(it, "perda_pct") ?? 0, gwMax = D(gw, "max_ms") ?? 0, jit = D(it, "jitter_ms") ?? 0;
            if (gwLoss > 1 || gwMax > 50)
                lines.Add(new(AvisoNivel.Problema, $"Rede local instável (até o roteador: {gwLoss:0.#}% de perda, pico de {gwMax:0} ms). Normal é 0% e menos de 5 ms: " +
                                                    "use cabo, ou aproxime do roteador e use Wi-Fi de 5 GHz."));
            else if (itLoss > 1)
                lines.Add(new(AvisoNivel.Atencao, $"{itLoss:0.#}% de perda de pacotes até a internet com a rede local boa: o problema está no provedor (lag e teleportes no jogo)."));
            else if (jit > 15)
                lines.Add(new(AvisoNivel.Atencao, $"Ping até a internet oscilando ({jit:0} ms de variação): pode causar lag; confira downloads/streams na mesma rede."));
            if (net["servidor"]?.GetValue<string>() is string srv && net["servidor_ping"] is JsonObject sp)
                lines.Add(new(null, $"Servidor do jogo ({srv}): ping médio {D(sp, "medio_ms"):0} ms, perda {D(sp, "perda_pct"):0.#}%."));
        }

        // 6) Temperaturas e erros de hardware
        double cpuT = D(r, "cpu_temp_max_c") ?? 0, gpuT = D(r, "gpu_temp_max_c") ?? 0;
        string cpuName = ctx?.Cpu ?? r["cpu"]?.ToString() ?? "";
        double cpuLimit = Regex.IsMatch(cpuName, @"Ryzen \d 7\d{3}|Ryzen \d 9\d{3}|i\d-1[34]\d{3}|Core Ultra", RegexOptions.IgnoreCase) ? 95 : 90;
        if (cpuT >= cpuLimit) lines.Add(new(AvisoNivel.Atencao, $"CPU chegou a {cpuT:0} °C: rode o TESTE DE REFRIGERAÇÃO (RESET DESEMPENHO › Stress test)."));
        if (gpuT >= 85) lines.Add(new(AvisoNivel.Atencao, $"GPU chegou a {gpuT:0} °C: melhore a ventilação do gabinete ou ajuste a curva das ventoinhas da placa."));
        if ((D(r, "erros_whea") ?? 0) > 0)
            lines.Add(new(AvisoNivel.Problema, $"{D(r, "erros_whea"):0} erro(s) de hardware (WHEA) durante o jogo: instabilidade de CPU/RAM (overclock, XMP agressivo, temperatura). " +
                                                "Rode a verificação de estabilidade em RESET OC."));
        if ((D(r, "resets_driver_video") ?? 0) > 0)
            lines.Add(new(AvisoNivel.Problema, $"O driver de vídeo reiniciou {D(r, "resets_driver_video"):0} vez(es): overclock da GPU instável, driver corrompido ou fonte no limite."));

        // 7) Programas em segundo plano, mouse e custo da gravação
        if (D(r, "outros_gpu_medio_pct") is double og && og >= 10)
            lines.Add(new(AvisoNivel.Atencao, $"Outros programas usaram {og:0}% da GPU em média durante o jogo (navegador com vídeo, gravação, overlay): feche-os para ganhar FPS."));
        if (r["processos_mais_pesados"] is JsonArray heavy && heavy.FirstOrDefault() is JsonObject h && (D(h, "cpu_medio_pct") ?? 0) >= 5)
            lines.Add(new(AvisoNivel.Dica, $"{h["processo"]} usou {D(h, "cpu_medio_pct"):0.#}% da CPU em média enquanto você jogava."));
        if (r["mouse"] is JsonObject mouse && (D(mouse, "padrao") ?? 9999) <= 500)
            lines.Add(new(AvisoNivel.Dica, $"Mouse a {D(mouse, "padrao"):0} Hz durante o jogo: se ele suporta 1000 Hz, aumente no software do fabricante."));
        if (D(r, "reset_explode_cpu_medio_pct") is double me)
            lines.Add(new(null, $"Custo da gravação: o RESET EXPLODE usou {me:0.##}% da CPU."));

        // 7b) Sugestões de experimento (regras com a evidência medida)
        try
        {
            if (DiagnosisRules.FromSession(folder) is { } facts)
                foreach (var sug in DiagnosisRules.Evaluate(facts)) lines.Add(new(AvisoNivel.Dica, "SUGESTÃO · " + sug.Text));
        }
        catch { /* diagnóstico é um extra: nunca derruba o laudo */ }

        // 8) Avisos de configuração do início da sessão
        if (r["avisos"] is JsonArray avisos)
            foreach (var a in avisos.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0))
                lines.Add(ParseAviso(a));

        string verdict = lines.Any(l => l.Nivel == AvisoNivel.Problema) ? "Ruim" : lines.Any(l => l.Nivel == AvisoNivel.Atencao) ? "Atenção" : "Bom";
        var ordered = lines.OrderBy(l => l.Nivel switch { AvisoNivel.Problema => 0, AvisoNivel.Atencao => 1, null => 2, _ => 3 }).ToList();
        return new Laudo(verdict, ordered, causes);
    }

    // ------------------------------------------------------------------ engasgos

    /// <summary>Causa de cada engasgo pelo segundo em que ele aconteceu (mesmo t_s de eventos.csv e sensores.csv).</summary>
    public static Dictionary<string, int> AttributeStutters(string folder, SessionContext? ctx, out string? topBackground)
    {
        topBackground = null;
        var result = new Dictionary<string, int>();
        var ev = CsvTable.Read(Path.Combine(folder, "eventos.csv"));
        var sens = CsvTable.Read(Path.Combine(folder, "sensores.csv"), c => c is "t_s" or "primeiro_plano" or "limitador" or "gpu_perfcap"
            or "jogo_io_leitura_mb_s" or "outros_cpu_pct" or "outros_gpu_pct");
        if (ev == null || sens == null) return result;
        var exib = CsvTable.Read(Path.Combine(folder, "exibicao.csv"), c => c is "t_s" or "gargalo");
        var procs = CsvTable.Read(Path.Combine(folder, "processos.csv"));

        var stutterTimes = ev.Rows.Where(row => ev.Get(row, "tipo") == "engasgo").Select(row => ev.Num(row, "t_s")).OfType<double>().ToList();
        var switchTimes = ev.Rows.Where(row => ev.Get(row, "tipo") is "segundo_plano" or "primeiro_plano").Select(row => ev.Num(row, "t_s")).OfType<double>().ToList();
        if (stutterTimes.Count == 0) return result;

        var times = sens.Rows.Select(row => sens.Num(row, "t_s") ?? double.NaN).ToArray();
        var io = sens.Rows.Select(row => sens.Num(row, "jogo_io_leitura_mb_s") ?? 0).ToList();
        var ioSorted = io.OrderBy(x => x).ToList();
        double ioMedian = ioSorted.Count > 0 ? ioSorted[ioSorted.Count / 2] : 0, ioThreshold = Math.Max(30, ioMedian * 5);
        var otherCpu = sens.Rows.Select(row => sens.Num(row, "outros_cpu_pct") ?? 0).OrderBy(x => x).ToList();
        double otherCpuThreshold = Math.Max(15, (otherCpu.Count > 0 ? otherCpu[otherCpu.Count / 2] : 0) + 10);
        var backgroundCounts = new Dictionary<string, int>();

        foreach (double t in stutterTimes)
        {
            int i = Nearest(times, t);
            if (i < 0) { Add(result, "sem_causa"); continue; }
            var row = sens.Rows[i];
            string cause;
            // Troca de janela trava o jogo no segundo dela ou no seguinte; engasgo antes da troca não é causado por ela.
            if (sens.Get(row, "primeiro_plano") == "0" || switchTimes.Any(s => t >= s - 0.6 && t <= s + 1.6)) cause = "alt_tab";
            else if ((sens.Num(row, "jogo_io_leitura_mb_s") ?? 0) >= ioThreshold) cause = "disco";
            else if (sens.Get(row, "limitador") == "Vram") cause = "vram";
            else if (sens.Get(row, "limitador") == "Ram") cause = "ram";
            else if (sens.Get(row, "limitador") == "Thermal" || (sens.Get(row, "gpu_perfcap") ?? "").Contains("Térmico")) cause = "termico";
            else if ((sens.Num(row, "outros_cpu_pct") ?? 0) >= otherCpuThreshold || (sens.Num(row, "outros_gpu_pct") ?? 0) >= 20)
            {
                cause = "segundo_plano";
                if (procs != null && BusiestNear(procs, t) is string name) backgroundCounts[name] = backgroundCounts.GetValueOrDefault(name) + 1;
            }
            else if (sens.Get(row, "limitador") == "Cpu" || (exib != null && exib.Rows.FirstOrDefault(x => exib.Num(x, "t_s") is double et && Math.Abs(et - Math.Floor(t)) < 0.5) is string[] er
                     && exib.Get(er, "gargalo") == "CPU"))
                cause = "cpu";
            else cause = "sem_causa";
            Add(result, cause);
        }
        topBackground = backgroundCounts.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        return result;
    }

    private static string? BusiestNear(CsvTable procs, double t) =>
        procs.Rows.Where(row => procs.Num(row, "t_s") is double pt && pt >= t - 5 && pt <= t + 5)
                  .OrderByDescending(row => procs.Num(row, "cpu_pct") ?? 0).Select(row => procs.Get(row, "processo")).FirstOrDefault();

    private static int Nearest(double[] times, double t)
    {
        int best = -1; double bestD = 0.75;
        for (int i = 0; i < times.Length; i++)
        {
            double d = Math.Abs(times[i] - t);
            if (d <= bestD) { best = i; bestD = d; }
        }
        return best;
    }

    private static void Add(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;

    public static string CauseLabel(string cause, string? background) => cause switch
    {
        "alt_tab" => "na troca de janela (alt-tab)",
        "disco" => "com o jogo lendo do disco",
        "vram" => "com a VRAM cheia",
        "ram" => "com a RAM cheia",
        "termico" => "com limite de temperatura",
        "segundo_plano" => background != null ? $"com outro programa pesado ({background})" : "com outro programa pesado",
        "cpu" => "com a CPU no limite",
        _ => "sem causa visível nos sensores",
    };

    private static string? CauseAdvice(string cause, SessionContext? ctx, string? background) => cause switch
    {
        "disco" => ctx?.Disco?.Tipo == "HD"
            ? "Engasgos de carregamento com o jogo num HD: mover para um SSD resolve a maior parte."
            : "Engasgos ao ler do disco são carregamento/streaming de texturas: comum em jogos de mundo aberto. Se a VRAM estiver perto do limite, baixe a qualidade das texturas.",
        "vram" => BottleneckAnalyzer.Advice(Limiter.Vram),
        "ram" => BottleneckAnalyzer.Advice(Limiter.Ram),
        "termico" => BottleneckAnalyzer.Advice(Limiter.Thermal),
        "segundo_plano" => $"{background ?? "Um programa em segundo plano"} pesou na hora dos engasgos: feche-o enquanto joga (RESET OTIMIZA fecha vários de uma vez).",
        "cpu" => "Engasgos com a CPU no limite: limite o FPS um pouco abaixo do que ela mantém e reduza opções pesadas para CPU (distância de visão, multidões, física).",
        "sem_causa" => "Engasgos sem causa nos sensores costumam ser compilação de shaders (somem ao repetir o mesmo trecho) ou do próprio jogo. " +
                       "Se começaram depois de atualizar o driver, limpe o cache de shaders em RESET OTIMIZA.",
        _ => null,
    };

    private static Linha ParseAviso(string a)
    {
        foreach (var (prefix, nivel) in new[] { ("PROBLEMA: ", AvisoNivel.Problema), ("ATENÇÃO: ", AvisoNivel.Atencao), ("DICA: ", AvisoNivel.Dica) })
            if (a.StartsWith(prefix)) return new(nivel, a[prefix.Length..]);
        return new(AvisoNivel.Dica, a);
    }

    // ------------------------------------------------------------------ comparação

    /// <summary>Compara a sessão <paramref name="before"/> com a <paramref name="after"/> (mesmo jogo): números e o que mudou no PC.</summary>
    public static List<string> Compare(string before, string after)
    {
        var a = Load(before); var b = Load(after);
        var lines = new List<string>();
        if (a == null || b == null) { lines.Add("Uma das sessões não tem resumo para comparar."); return lines; }

        double durA = D(a, "duracao_s") ?? 0, durB = D(b, "duracao_s") ?? 0;
        double spmA = (D(a, "engasgos") ?? 0) / Math.Max(1 / 60.0, durA / 60), spmB = (D(b, "engasgos") ?? 0) / Math.Max(1 / 60.0, durB / 60);
        Row(lines, "FPS médio", D(a, "fps_medio"), D(b, "fps_medio"), higherIsBetter: true, "0");
        Row(lines, "1% low", D(a, "low_1pct_fps"), D(b, "low_1pct_fps"), true, "0");
        Row(lines, "0,1% low", D(a, "low_01pct_fps"), D(b, "low_01pct_fps"), true, "0");
        Row(lines, "Engasgos por minuto", spmA, spmB, false, "0.#");
        Row(lines, "Frametime p99 (ms)", D(a["frametime_ms"] as JsonObject, "p99"), D(b["frametime_ms"] as JsonObject, "p99"), false, "0.#");
        Row(lines, "Latência até a tela (ms)", D((a["exibicao"] as JsonObject)?["latencia_exibicao_ms"] as JsonObject, "media"),
            D((b["exibicao"] as JsonObject)?["latencia_exibicao_ms"] as JsonObject, "media"), false, "0.#");
        Row(lines, "CPU máx. (°C)", D(a, "cpu_temp_max_c"), D(b, "cpu_temp_max_c"), false, "0");
        Row(lines, "GPU máx. (°C)", D(a, "gpu_temp_max_c"), D(b, "gpu_temp_max_c"), false, "0");

        var changes = ConfigChanges(before, after);
        lines.Add(changes.Count > 0 ? "O que mudou no PC: " + string.Join("; ", changes) + "." : "Nenhuma mudança de configuração detectada entre as duas sessões.");
        if (durA > 0 && durB > 0 && Math.Max(durA, durB) / Math.Min(durA, durB) > 2)
            lines.Add("As sessões têm durações bem diferentes: compare trechos parecidos do jogo (mesmo mapa/cena) para o resultado valer.");
        return lines;
    }

    /// <summary>O que mudou no PC entre duas sessões (ajustes, driver, Hz, RAM...), uma frase por mudança.</summary>
    public static List<string> ConfigChanges(string before, string after)
    {
        var ca = SessionContext.Load(before); var cb = SessionContext.Load(after);
        var changes = new List<string>();
        if (ca != null && cb != null)
        {
            foreach (var t in cb.AjustesLigados.Except(ca.AjustesLigados)) changes.Add($"ligou \"{t}\"");
            foreach (var t in ca.AjustesLigados.Except(cb.AjustesLigados)) changes.Add($"desligou \"{t}\"");
            Diff(changes, "driver da GPU", ca.DriverGpu, cb.DriverGpu);
            Diff(changes, "monitor", Hz(ca), Hz(cb));
            Diff(changes, "RAM", Ram(ca), Ram(cb));
            Diff(changes, "plano de energia", ca.PlanoEnergia, cb.PlanoEnergia);
            Diff(changes, "afinidade de núcleos", ca.Afinidade, cb.Afinidade);
            Diff(changes, "HAGS", ca.Hags?.ToString(), cb.Hags?.ToString());
            Diff(changes, "Resizable BAR", ca.ResizableBar?.ToString(), cb.ResizableBar?.ToString());
            Diff(changes, "BIOS", ca.BiosVersao, cb.BiosVersao);
            Diff(changes, "VBS", ca.Vbs?.ToString(), cb.Vbs?.ToString());
            Diff(changes, "Integridade de Memória", ca.Hvci?.ToString(), cb.Hvci?.ToString());
            Diff(changes, "Secure Boot", ca.SecureBoot?.ToString(), cb.SecureBoot?.ToString());
            Diff(changes, "versão do CS2", ca.Cs2Build, cb.Cs2Build);
            Diff(changes, "fps_max", ca.Cs2FpsMax, cb.Cs2FpsMax);
            Diff(changes, "opções de inicialização do CS2", ca.Cs2OpcoesInicio, cb.Cs2OpcoesInicio);
            Diff(changes, "perfil NVIDIA do CS2", ca.PerfilNvidia, cb.PerfilNvidia);
            Diff(changes, "monitores", string.Join(" + ", ca.Monitores), string.Join(" + ", cb.Monitores));
            if (ca.Cs2Video != null && cb.Cs2Video != null)
                foreach (var k in ca.Cs2Video.Keys.Union(cb.Cs2Video.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                    Diff(changes, "vídeo " + k.Replace("setting.", ""), ca.Cs2Video.GetValueOrDefault(k), cb.Cs2Video.GetValueOrDefault(k));
            var openA = ca.ProgramasAbertos.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var openB = cb.ProgramasAbertos.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (ca.ProgramasAbertos.Count > 0 && cb.ProgramasAbertos.Count > 0)
            {
                foreach (var n in openB.Except(openA)) changes.Add($"programa aberto a mais: {n}");
                foreach (var n in openA.Except(openB)) changes.Add($"programa fechado: {n}");
            }
        }
        return changes;
    }

    private static string? Hz(SessionContext c) => c.Monitor is { } m ? $"{m.Largura}×{m.Altura} a {m.HzAtual} Hz" : null;
    private static string? Ram(SessionContext c) => c.Ram.Count > 0 ? $"{c.Ram.Count}× {c.Ram[0].CapacidadeGb:0} GB a {c.Ram.Min(x => x.VelocidadeConfiguradaMts)} MT/s" : null;

    private static void Diff(List<string> changes, string what, string? a, string? b)
    {
        if (a != null && b != null && a != b) changes.Add($"{what}: {a} → {b}");
    }

    private static void Row(List<string> lines, string label, double? a, double? b, bool higherIsBetter, string fmt)
    {
        if (a is not double x || b is not double y) return;
        double diff = y - x, pct = x != 0 ? 100 * diff / Math.Abs(x) : 0;
        bool same = Math.Abs(pct) < 3 && Math.Abs(diff) < (fmt == "0" ? 1 : 0.5);
        string judge = same ? "igual" : (diff > 0) == higherIsBetter ? "melhor" : "pior";
        string change = x != 0 ? $"{(diff > 0 ? "+" : "")}{pct:0}%" : $"{(diff > 0 ? "+" : "")}{diff.ToString(fmt, CultureInfo.CurrentCulture)}";
        lines.Add($"{label}: {x.ToString(fmt, CultureInfo.CurrentCulture)} → {y.ToString(fmt, CultureInfo.CurrentCulture)}" +
                  (same ? " (igual)" : $" ({change}, {judge})"));
    }

    private static JsonObject? Load(string folder)
    {
        try { return JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "resumo.json")))?.AsObject(); }
        catch { return null; }
    }

    private static double? D(JsonObject? o, string k)
    {
        if (o?[k] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        return null;
    }
}

/// <summary>CSV com cabeçalho (vírgula, aspas como no RFC 4180), guardando só as colunas pedidas para poupar memória.</summary>
public sealed class CsvTable
{
    public readonly Dictionary<string, int> Index = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string[]> Rows = new();

    public static CsvTable? Read(string path, Func<string, bool>? keep = null)
    {
        if (!File.Exists(path)) return null;
        var t = new CsvTable();
        using var r = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string? header = r.ReadLine();
        if (header == null) return t;
        var cols = Split(header);
        var wanted = Enumerable.Range(0, cols.Count).Where(i => keep == null || keep(cols[i])).ToArray();
        for (int k = 0; k < wanted.Length; k++) t.Index[cols[wanted[k]]] = k;
        for (string? line = r.ReadLine(); line != null; line = r.ReadLine())
        {
            var f = Split(line);
            t.Rows.Add(wanted.Select(i => i < f.Count ? f[i] : "").ToArray());
        }
        return t;
    }

    public string? Get(string[] row, string col) => Index.TryGetValue(col, out int i) && i < row.Length ? row[i] : null;

    public double? Num(string[] row, string col) =>
        double.TryParse(Get(row, col), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static List<string> Split(string line)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { list.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        list.Add(sb.ToString());
        return list;
    }
}
