namespace ResetExplode;

/// <summary>Métricas de estabilidade do frametime, calculadas só sobre a lista gravada (dá para recalcular sessões antigas).</summary>
public static class FrameMetrics
{
    /// <summary>Desvio padrão do frametime (ms). 0 com menos de 2 quadros.</summary>
    public static double StdDev(IReadOnlyList<float> frameMs)
    {
        if (frameMs.Count < 2) return 0;
        double mean = 0;
        foreach (var f in frameMs) mean += f;
        mean /= frameMs.Count;
        double sum = 0;
        foreach (var f in frameMs) sum += (f - mean) * (f - mean);
        return Math.Sqrt(sum / (frameMs.Count - 1));
    }

    /// <summary>
    /// Instante (s desde o início) de cada queda: frame maior que <paramref name="factor"/> vezes a mediana do último 1 s.
    /// Sem o piso de 33,4 ms do "engasgo": a 300 fps uma queda para 100 fps (10 ms) também conta.
    /// </summary>
    public static List<double> Drops(IReadOnlyList<float> frameMs, double factor = 2.5)
    {
        var drops = new List<double>();
        int n = frameMs.Count;
        if (n < 20) return drops;

        var time = new double[n]; // instante em que cada frame terminou (ms)
        double acc = 0;
        for (int i = 0; i < n; i++) { acc += frameMs[i]; time[i] = acc; }

        int start = 0;
        double threshold = 0, lastCalc = double.NegativeInfinity;
        float[] buffer = new float[1024];
        for (int i = 0; i < n; i++)
        {
            while (time[i] - time[start] > 1000) start++;
            int len = i - start;
            if (len >= 20 && time[i] - lastCalc >= 100)
            {
                // shortcut: mediana recalculada a cada 100 ms, não a cada frame, para sessões longas; refazer por frame se a precisão pedir.
                if (buffer.Length < len) buffer = new float[len * 2];
                for (int k = 0; k < len; k++) buffer[k] = frameMs[start + k];
                Array.Sort(buffer, 0, len);
                threshold = factor * buffer[len / 2];
                lastCalc = time[i];
            }
            if (threshold > 0 && frameMs[i] > threshold) drops.Add(time[i] / 1000);
        }
        return drops;
    }

    /// <summary>
    /// Quanto as quedas são periódicas: coeficiente de variação dos intervalos entre elas (quedas a menos de 0,25 s uma da outra
    /// contam como uma). Menor = mais regular (assinatura de stutter do fTPM). <c>null</c> com menos de 4 quedas.
    /// </summary>
    public static double? RegularityScore(IReadOnlyList<double> dropTimes)
    {
        var events = new List<double>();
        foreach (var t in dropTimes)
            if (events.Count == 0 || t - events[^1] >= 0.25) events.Add(t);
        if (events.Count < 4) return null;

        var gaps = new List<double>();
        for (int i = 1; i < events.Count; i++) gaps.Add(events[i] - events[i - 1]);
        double mean = gaps.Average();
        if (mean <= 0) return null;
        double var = gaps.Sum(g => (g - mean) * (g - mean)) / gaps.Count;
        return Math.Sqrt(var) / mean;
    }
}
