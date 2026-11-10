using System.Diagnostics;

namespace ResetExplode;

/// <summary>O benchmark só roda offline: se o FACEIT Anti-Cheat estiver aberto, a captura se recusa a começar.</summary>
public static class AntiCheatGuard
{
    /// <summary>Nome do processo (sem .exe, sem diferenciar maiúsculas) começando com "faceit": cliente, serviço e o anti-cheat.</summary>
    public static bool IsRunning(IEnumerable<string> processNames) =>
        processNames.Any(n => n.Trim().StartsWith("faceit", StringComparison.OrdinalIgnoreCase));

    public static bool IsRunning()
    {
        try
        {
            var all = Process.GetProcesses();
            try { return IsRunning(all.Select(p => p.ProcessName)); }
            finally { foreach (var p in all) p.Dispose(); }
        }
        catch { return false; }
    }
}
