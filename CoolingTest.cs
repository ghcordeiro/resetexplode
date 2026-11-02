namespace ResetExplode;

/// <summary>
/// Teste de refrigeração da CPU: 1 min em repouso, 5 min de carga em todos os núcleos e 1 min esfriando.
/// Pela forma da curva separa problema no bloco (pasta, aperto, fluxo da bomba) de radiador saturado (ventoinhas, poeira, líquido).
/// Não lê a bomba: a biblioteca de sensores não enxerga controladoras como o Corsair Commander Core.
/// Os limites são heurísticos, por isso o veredito fala em "provável".
/// </summary>
public sealed class CoolingTest
{
    public const int IdleSeconds = 60, LoadSeconds = 300, CooldownSeconds = 60;

    public enum Phase { Idle, Load, Cooldown, Done }

    /// <param name="T">Segundos desde o início da fase.</param>
    /// <param name="Fan">Média de RPM das ventoinhas da placa-mãe (null se nenhuma é lida).</param>
    public readonly record struct Sample(double T, float Temp, float? Power, float? Fan);

    public Phase Current { get; private set; } = Phase.Idle;
    public DateTime PhaseStart { get; private set; } = DateTime.Now;
    public List<Sample> Idle { get; } = new();
    public List<Sample> Load { get; } = new();
    public List<Sample> Cool { get; } = new();
    /// <summary>Segundos de carga até bater o limite de temperatura (null = não bateu).</summary>
    public double? HitLimitAt { get; private set; }

    public double Elapsed(DateTime now) => (now - PhaseStart).TotalSeconds;

    public void Add(DateTime now, float temp, float? power, float? fan)
    {
        var s = new Sample(Elapsed(now), temp, power, fan);
        (Current switch { Phase.Idle => Idle, Phase.Load => Load, _ => Cool }).Add(s);
    }

    public void Next(DateTime now, bool hitLimit = false)
    {
        if (Current == Phase.Load && hitLimit) HitLimitAt = Elapsed(now);
        Current = Current switch { Phase.Idle => Phase.Load, Phase.Load => Phase.Cooldown, _ => Phase.Done };
        PhaseStart = now;
    }

    // ------------------------------------------------------------------ análise

    public sealed record Result(string Verdict, IReadOnlyList<string> Lines);

    public Result Analyze() => Analyze(Idle, Load, Cool, HitLimitAt);

    public static Result Analyze(IReadOnlyList<Sample> idle, IReadOnlyList<Sample> load, IReadOnlyList<Sample> cool, double? hitLimitAt)
    {
        var lines = new List<string>();
        int bad = 0, warn = 0;

        float idleTemp = Median(idle.Where(s => s.T >= IdleSeconds / 2.0).Select(s => s.Temp)) ?? Median(idle.Select(s => s.Temp)) ?? 0;
        double end = load.Count > 0 ? load.Max(x => x.T) : 0;
        var last = load.Where(s => s.T >= end - 60).ToList(); // último minuto de carga
        float peak = load.Count > 0 ? load.Max(s => s.Temp) : idleTemp;
        float plateau = Median(last.Select(s => s.Temp)) ?? peak;
        float? power = Median(last.Where(s => s.Power is > 0).Select(s => s.Power!.Value));
        float? t10 = load.FirstOrDefault(s => s.T >= 10) is { Temp: > 0 } f ? f.Temp : null;

        lines.Add($"Repouso {idleTemp:0} °C · com carga {(hitLimitAt != null ? $"bateu o limite em {hitLimitAt:0} s" : $"estabilizou em {plateau:0} °C")}" +
                  (power is float w ? $" com {w:0} W" : "") +
                  (power is float pw && pw > 20 && hitLimitAt == null ? $" ({(plateau - idleTemp) / pw:0.00} °C/W)" : "") + ".");

        // 1) repouso
        if (idleTemp >= 50)
        {
            bad++;
            lines.Add($"Repouso alto ({idleTemp:0} °C; o normal com watercooler é 30–45 °C): provável contato ruim (pasta seca, bloco torto ou mal apertado) ou o líquido não circula.");
        }

        // 2) forma da subida
        if (hitLimitAt is double hit && hit <= 30)
        {
            bad++;
            lines.Add($"Bateu o limite em {hit:0} s: o calor não sai do bloco. Provável pasta térmica, aperto do bloco ou pouco fluxo da bomba (bomba gira, mas com ar ou desgastada).");
        }
        else if (hitLimitAt is double hit2)
        {
            bad++;
            lines.Add($"Subiu aos poucos e bateu o limite em {hit2 / 60:0.#} min: provável radiador saturado (ventoinhas lentas ou do lado errado, poeira, líquido evaporado).");
        }
        else if (plateau >= 85)
        {
            warn++;
            lines.Add($"Estabilizou alto ({plateau:0} °C): o radiador está no limite. Ryzen 7000/9000 e Intel 13ª/14ª geração sobem de propósito até ~95 °C em carga total; nelas isto pode ser normal se o consumo estiver no máximo da CPU.");
        }
        else lines.Add($"Temperatura com carga total ok ({plateau:0} °C).");

        if (t10 is float early && hitLimitAt is null && early - idleTemp >= 40)
        {
            warn++;
            lines.Add($"Subiu {early - idleTemp:0} °C nos primeiros 10 s: sinal de contato ruim entre CPU e bloco (comum em Ryzen 7000 mesmo bem montado; se as outras leituras estão boas, ignore).");
        }

        // 3) ventoinhas da placa-mãe
        float? fanIdle = Median(idle.Where(s => s.Fan is > 0).Select(s => s.Fan!.Value));
        float? fanLoad = last.Where(s => s.Fan is > 0).Select(s => s.Fan!.Value).DefaultIfEmpty().Max() is float fl && fl > 0 ? fl : null;
        if (end < 60) { } // carga curta demais para as ventoinhas reagirem: não opina
        else if (fanIdle is float fi && fanLoad is float fload)
        {
            if (fload < fi * 1.15 && peak >= 70)
            {
                warn++;
                lines.Add($"As ventoinhas da placa-mãe não aceleraram ({fi:0} → {fload:0} RPM) com a CPU a {peak:0} °C: ajuste a curva na BIOS.");
            }
            else lines.Add($"Ventoinhas da placa-mãe aceleraram ({fi:0} → {fload:0} RPM).");
        }
        else lines.Add("Nenhuma ventoinha lida pela placa-mãe (ligadas no controlador do cooler, como o Commander Core, ficam invisíveis aqui).");

        // 4) esfriamento
        float stopTemp = load.Count > 0 ? load[^1].Temp : peak;
        // Se bateu o limite logo no início só o bloco esquentou: o esfriamento rápido não diz nada.
        if (hitLimitAt is not <= 30 && cool.FirstOrDefault(s => s.T >= 30) is { Temp: > 0 } c30)
        {
            float drop = stopTemp - c30.Temp;
            if (drop >= 20) lines.Add($"Esfriou {drop:0} °C em 30 s: o bloco troca calor bem.");
            else if (stopTemp >= 75)
            {
                warn++;
                lines.Add($"Esfriou só {drop:0} °C em 30 s: o líquido ficou quente. Provável radiador/ventoinhas fracos ou pouco fluxo.");
            }
        }

        string verdict = bad > 0 ? "Ruim" : warn > 0 ? "Atenção" : "Bom";
        return new Result(verdict, lines);
    }

    private static float? Median(IEnumerable<float> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? null : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }
}
