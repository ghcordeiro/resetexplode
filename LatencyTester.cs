using System.Globalization;

namespace ResetExplode;

/// <summary>Canal com o testador (Arduino pela serial). Uma linha por comando, uma linha de resposta.</summary>
public interface ILatencyLink
{
    /// <summary>Manda a linha e espera a resposta (null = sem resposta no tempo).</summary>
    Task<string?> CommandAsync(string line, int timeoutMs, CancellationToken ct);
    /// <summary>Manda a linha sem esperar resposta.</summary>
    void Send(string line);
    /// <summary>Lê a próxima linha (null = nada no tempo).</summary>
    Task<string?> ReadLineAsync(int timeoutMs, CancellationToken ct);
    /// <summary>Manda um caractere solto, o mais rápido possível (marca o instante do frame branco).</summary>
    void SendRaw(char c);
}

/// <summary>Tela de teste: preta, fica branca no clique (ou quando mandada).</summary>
public interface ILatencyScreen
{
    bool White { get; set; }
    /// <summary>Apresenta um frame branco chamando <paramref name="beforePresent"/> logo antes do Present.</summary>
    void FlashWith(Action beforePresent);
}

public enum LatencyMode { Click, Display }

/// <summary>Último resultado do testador, guardado nas configurações e copiado para o contexto de cada sessão.</summary>
public sealed class LatencyRecord
{
    public DateTime Data { get; set; }
    public double? CliqueTelaMs { get; set; }
    public double? PcTelaMs { get; set; }
    /// <summary>Clique até a tela − PC até a tela: USB do mouse + Windows + aplicativo.</summary>
    public double? EntradaMs => CliqueTelaMs is double c && PcTelaMs is double p ? Math.Round(c - p, 1) : null;
    public double? RespostaPixelMs { get; set; }
    public string Metodo { get; set; } = "arduino";
}

public sealed record LatencyStats(LatencyMode Mode, int Samples, int Timeouts, double MinMs, double MedianMs, double AvgMs,
                                  double P95Ms, double MaxMs, double StdMs, double? RiseMedianMs)
{
    public string Title => Mode == LatencyMode.Click ? "Clique até a tela" : "PC até a tela";
    public string Text =>
        $"{Title}: mediana {MedianMs:0.0} ms (mín. {MinMs:0.0}, p95 {P95Ms:0.0}, máx. {MaxMs:0.0}, variação ±{StdMs:0.0}) em {Samples} medições" +
        (Timeouts > 0 ? $", {Timeouts} sem resposta" : "") +
        (RiseMedianMs is double r ? $". Resposta do pixel (preto→branco, 10–90%): {r:0.0} ms." : ".");
}

/// <summary>
/// Teste de atraso com o testador Arduino (Arduino/ResetLatencia). Calibra o sensor com a tela preta e branca, depois
/// repete N vezes: <see cref="LatencyMode.Click"/> = o Arduino clica e mede até a tela clarear (atraso de ponta a ponta);
/// <see cref="LatencyMode.Display"/> = o PC apresenta um frame branco e avisa o Arduino na hora (atraso do PC até a tela:
/// fila do driver, composição, monitor). A diferença entre os dois é o atraso da entrada (USB + Windows + jogo).
/// </summary>
public sealed class LatencyTester
{
    private readonly ILatencyLink _link;
    private readonly ILatencyScreen _screen;
    private readonly Random _rnd = new();
    public Func<int, Task> Delay { get; init; } = ms => Task.Delay(ms);

    public LatencyTester(ILatencyLink link, ILatencyScreen screen) { _link = link; _screen = screen; }

    public static bool IsTester(string? pong) => pong?.StartsWith("PONG RESETLAT", StringComparison.Ordinal) == true;

    /// <summary>Lê o sensor com a tela preta e branca e grava os níveis no Arduino. Devolve (preto, branco).</summary>
    public async Task<(int Black, int White)> CalibrateAsync(CancellationToken ct)
    {
        _screen.White = false;
        await Delay(600);
        int black = Level(await _link.CommandAsync("LVL", 1500, ct));
        _screen.White = true;
        await Delay(400);
        int white = Level(await _link.CommandAsync("LVL", 1500, ct));
        _screen.White = false;
        await Delay(400);
        if (Math.Abs(white - black) < 30)
            throw new InvalidOperationException($"O sensor quase não viu diferença entre preto ({black}) e branco ({white}). " +
                "Encoste o sensor no centro da tela, tape a luz ambiente e deixe o brilho do monitor no máximo.");
        if (await _link.CommandAsync($"THR {black} {white}", 1500, ct) != "OK")
            throw new InvalidOperationException("O testador não confirmou a calibração.");
        return (black, white);
    }

    public async Task<LatencyStats> RunAsync(LatencyMode mode, int samples, Action<int, double?>? progress, CancellationToken ct)
    {
        await CalibrateAsync(ct);
        var lat = new List<double>(); var rise = new List<double>(); int timeouts = 0;
        for (int i = 0; i < samples; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Delay(_rnd.Next(150, 400)); // intervalo aleatório: não sincroniza com a taxa do monitor
            string? r;
            if (mode == LatencyMode.Click) r = await _link.CommandAsync("CLICK", 1500, ct);
            else
            {
                _link.Send("WAIT");
                await Delay(30);
                _screen.FlashWith(() => _link.SendRaw('F'));
                r = await _link.ReadLineAsync(2500, ct);
            }
            _screen.White = false;
            if (ParseResult(r) is { } parsed)
            {
                var (us, riseUs) = parsed;
                lat.Add(us / 1000);
                if (riseUs is double ru) rise.Add(ru / 1000);
                progress?.Invoke(i + 1, us / 1000);
            }
            else { timeouts++; progress?.Invoke(i + 1, null); }
            await Delay(120); // a tela volta ao preto antes da próxima medição
        }
        if (lat.Count == 0) throw new InvalidOperationException("Nenhuma medição: o sensor não viu a tela mudar. Confira a calibração e se a janela de teste está na frente.");
        return Stats(mode, lat, rise, timeouts);
    }

    /// <summary>"R 12345 2100" → (12345 µs, 2100 µs); "R 12345 -1" → (12345, null); "R TIMEOUT"/lixo → null.</summary>
    public static (double Us, double? RiseUs)? ParseResult(string? line)
    {
        var p = (line ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 2 || p[0] != "R" || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double us) || us <= 0) return null;
        double? rise = p.Length >= 3 && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) && r > 0 ? r : null;
        return (us, rise);
    }

    private static int Level(string? line)
    {
        var p = (line ?? "").Trim().Split(' ');
        if (p.Length == 2 && p[0] == "L" && int.TryParse(p[1], out int v)) return v;
        throw new InvalidOperationException($"Resposta inesperada do testador: \"{line}\".");
    }

    public static LatencyStats Stats(LatencyMode mode, List<double> latMs, List<double> riseMs, int timeouts)
    {
        var s = latMs.OrderBy(x => x).ToList();
        double P(double q) => s[(int)Math.Min(s.Count - 1, Math.Floor(s.Count * q))];
        double avg = s.Average();
        double std = Math.Sqrt(s.Sum(x => (x - avg) * (x - avg)) / s.Count);
        double? riseMed = riseMs.Count > 0 ? riseMs.OrderBy(x => x).ElementAt(riseMs.Count / 2) : null;
        return new LatencyStats(mode, s.Count, timeouts, s[0], P(0.5), avg, P(0.95), s[^1], std, riseMed);
    }
}
