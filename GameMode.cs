using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>Opções do Modo CS automático (gravadas em settings.json).</summary>
public sealed class GameModeOptions
{
    public bool Enabled { get; set; }
    public bool Desktop { get; set; } = true;
    public string DeskRes { get; set; } = "1280x960";
    public bool CloseApps { get; set; } = true;
    public bool CleanMemory { get; set; } = true;
    public bool StopServices { get; set; } = true;
    public bool HighPriority { get; set; } = true;
    public bool PowerPlan { get; set; } = true;
}

/// <summary>O que foi mudado ao abrir o jogo, gravado ANTES de cada mudança para desfazer mesmo se o app cair.</summary>
public sealed class GameModeState
{
    public string? Desktop { get; set; }              // "LxA@Hz" anterior
    public List<string> StoppedServices { get; set; } = new();
    public string? PowerPlan { get; set; }            // GUID do plano anterior
}

/// <summary>
/// Modo CS automático: quando o cs2.exe abre (de qualquer jeito), deixa o Windows capado só durante o jogo; quando fecha, desfaz.
/// Nada aqui é permanente: serviços são parados (não desativados) e voltam a rodar, plano e resolução voltam ao anterior.
/// </summary>
public static class GameMode
{
    public static readonly string[] Services = { "SysMain", "WSearch", "Spooler", "wuauserv" };
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private static string StatePath => Path.Combine(AppSettings.Folder, "gamemode-restore.json");
    private static readonly object Lock = new();

    public static bool Active => File.Exists(StatePath);

    /// <summary>Aplica as ações escolhidas. <paramref name="keepApps"/> = não fechar programas (perfil "uso real"). Devolve o que foi feito.</summary>
    public static List<string> Apply(GameModeOptions o, int pid, bool keepApps)
    {
        var log = new List<string>();
        lock (Lock)
        {
            var st = new GameModeState();
            Save(st);

            bool gameOld = false;
            try { using var gp = Process.GetProcessById(pid); gameOld = DateTime.Now - gp.StartTime > TimeSpan.FromSeconds(8); } catch { }
            if (o.Desktop && gameOld) log.Add("resolução não trocada (o jogo já estava aberto; troque antes de abrir para não atrapalhar a tela cheia)");
            else if (o.Desktop && Regex.Match(o.DeskRes ?? "", @"^(\d+)x(\d+)$") is { Success: true } m)
            {
                var (prev, err) = DisplayMode.SwitchPrimary(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                if (prev != null) { st.Desktop = $"{prev.Width}x{prev.Height}@{prev.Hz}"; Save(st); log.Add($"área de trabalho em {o.DeskRes}"); }
                else if (err != null) log.Add($"resolução não trocada ({err})");
            }

            if (o.PowerPlan && WindowsTweaks.ActiveScheme() is string plan && !plan.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase))
            {
                st.PowerPlan = plan; Save(st);
                if (!WindowsTweaks.PowerCfg("/list").Contains(HighPerformance, StringComparison.OrdinalIgnoreCase))
                    WindowsTweaks.PowerCfg($"-duplicatescheme {HighPerformance} {HighPerformance}");
                WindowsTweaks.PowerCfg($"/setactive {HighPerformance}");
                log.Add("plano Alto desempenho");
            }

            if (o.StopServices)
                foreach (var svc in Services.Where(IsRunning))
                {
                    st.StoppedServices.Add(svc); Save(st);
                    Sc($"stop \"{svc}\"");
                    log.Add($"{svc} parado");
                }

            if (o.HighPriority)
                try { using var p = Process.GetProcessById(pid); p.PriorityClass = ProcessPriorityClass.High; log.Add("CS2 em prioridade alta"); }
                catch { log.Add("prioridade não alterada (o jogo negou)"); }
        }

        if (o.CloseApps && !keepApps)
        {
            var close = Optimizer.ListApps().Where(a => a.HasWindow && a.ProcessName != "cs2" && !StartupPolicy.IsAllowed(a.ProcessName, a.Title)).ToList();
            if (close.Count > 0)
            {
                var r = Optimizer.CloseApps(close);
                if (r.Closed.Count > 0) log.Add("fechados: " + string.Join(", ", r.Closed));
                if (r.NotClosed.Count > 0) log.Add("não fecharam (algo não salvo?): " + string.Join(", ", r.NotClosed));
            }
        }
        if (o.CleanMemory)
        {
            var mem = Optimizer.CleanMemory();
            log.Add($"memória liberada: {mem.FreedBytes / 1048576.0:N0} MB");
        }
        return log;
    }

    /// <summary>Desfaz tudo que está no arquivo de estado (chamado quando o jogo fecha e na abertura do app).</summary>
    public static List<string> Undo()
    {
        var log = new List<string>();
        lock (Lock)
        {
            if (!File.Exists(StatePath)) return log;
            GameModeState? st = null;
            try { st = JsonSerializer.Deserialize<GameModeState>(File.ReadAllText(StatePath)); } catch { }
            if (st != null)
            {
                if (st.Desktop != null && st.Desktop.Split('x', '@') is [var w, var h, var hz] && int.TryParse(w, out int wi) && int.TryParse(h, out int hi) && int.TryParse(hz, out int hzi))
                {
                    try { DisplayMode.Restore(new DisplayMode.Mode(wi, hi, hzi)); log.Add("resolução restaurada"); } catch { }
                }
                if (st.PowerPlan != null) { try { WindowsTweaks.PowerCfg($"/setactive {st.PowerPlan}"); log.Add("plano de energia restaurado"); } catch { } }
                foreach (var svc in st.StoppedServices) { Sc($"start \"{svc}\""); log.Add($"{svc} religado"); }
            }
            try { File.Delete(StatePath); } catch { }
        }
        return log;
    }

    private static void Save(GameModeState st)
    {
        Directory.CreateDirectory(AppSettings.Folder);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(st));
    }

    /// <summary>"sc query" mostra o estado com o número 4 para RUNNING (o número não muda com o idioma do Windows).</summary>
    private static bool IsRunning(string svc) => Regex.IsMatch(Sc($"query \"{svc}\""), @":\s+4\s+RUNNING", RegexOptions.IgnoreCase);

    private static string Sc(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return o;
        }
        catch { return ""; }
    }
}
