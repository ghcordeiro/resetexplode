using System.Globalization;

namespace ResetExplode;

/// <summary>
/// "Vale comprar?" com as regras do PRD aplicadas aos dados gravados:
/// 5500X3D só se a linha de base (já com os ajustes grátis) tem 1% low abaixo da taxa do monitor com a CPU como limite na maioria das execuções;
/// 32 GB só se o perfil de uso real (programas do dia a dia abertos) passa de 85% de RAM ou pagina nas quedas.
/// </summary>
public static class PurchaseVerdict
{
    public static List<string> Evaluate(string baselineProfile, string? realUseProfile)
    {
        var lines = new List<string>();
        string N0(double v) => v.ToString("0", CultureInfo.CurrentCulture);

        var runs = SessionProfile.Runs(baselineProfile).Where(r => r.Valid && !r.WarmUp).ToList();
        if (runs.Count < ProfileComparer.MinRuns)
        {
            lines.Add($"Processador: meça a linha de base \"{baselineProfile}\" mais {ProfileComparer.MinRuns - runs.Count} vez(es) antes de decidir.");
        }
        else
        {
            var facts = runs.Select(r => DiagnosisRules.FromSession(r.Folder)).OfType<DiagFacts>().ToList();
            int cpu = facts.Count(f => DiagnosisRules.Evaluate(f).Any(s => s.Rule.StartsWith("Limite de CPU")));
            int hz = facts.Select(f => f.MonitorHz).DefaultIfEmpty(0).Max();
            double low1 = ProfileComparer.Summarize("1% low", runs.Select(r => r.Low1Fps)).Median;
            if (hz == 0) lines.Add("Processador: sem a taxa do monitor nas sessões; não dá para aplicar a regra.");
            else if (low1 < hz && cpu * 2 > facts.Count)
                lines.Add($"Processador: o 5500X3D se justifica pelos números. 1% low mediano de {N0(low1)} fps, abaixo dos {hz} Hz do monitor, " +
                          $"com a CPU como limite em {cpu} de {facts.Count} execuções. Antes de comprar, confira que os ajustes grátis da matriz já foram testados.");
            else if (low1 >= hz)
                lines.Add($"Processador: não precisa. O 1% low mediano ({N0(low1)} fps) já alcança a taxa do monitor ({hz} Hz).");
            else
                lines.Add($"Processador: ainda não. O 1% low ({N0(low1)} fps) está abaixo de {hz} Hz, mas a CPU foi o limite em só {cpu} de {facts.Count} execuções: " +
                          "o gargalo está em outro lugar (veja as sugestões do laudo).");
        }

        if (realUseProfile == null)
        {
            lines.Add("Memória: grave um perfil com \"uso real\" no nome, com os programas do dia a dia abertos (Discord em call, navegador), para decidir sobre 32 GB.");
            return lines;
        }
        var real = SessionProfile.Runs(realUseProfile).Where(r => r.Valid && !r.WarmUp).ToList();
        var rf = real.Select(r => DiagnosisRules.FromSession(r.Folder)).OfType<DiagFacts>().ToList();
        if (rf.Count == 0) { lines.Add($"Memória: o perfil \"{realUseProfile}\" ainda não tem execução válida."); return lines; }
        double ram = rf.Max(f => f.RamUsedPct ?? 0);
        int paging = rf.Count(f => f.PagingAtDropsPct >= 50);
        lines.Add(ram > DiagnosisRules.MemoryAbove || paging * 2 > rf.Count
            ? $"Memória: 32 GB se justificam. No uso real a RAM chegou a {N0(ram)}% e houve paginação nas quedas em {paging} de {rf.Count} execuções."
            : $"Memória: 16 GB bastam por enquanto. No uso real a RAM chegou a {N0(ram)}% e a paginação apareceu nas quedas em {paging} de {rf.Count} execuções.");
        return lines;
    }
}
