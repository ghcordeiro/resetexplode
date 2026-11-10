namespace ResetExplode;

/// <summary>O que pode continuar abrindo com o Windows num PC dedicado ao CS2. Todo o resto é desativado (reversível).</summary>
public static class StartupPolicy
{
    /// <summary>Trechos do nome ou do comando (sem diferenciar maiúsculas) dos programas que ficam: Steam, Discord, Razer (DPI do mouse), áudio, NVIDIA, FACEIT e segurança do Windows.</summary>
    public static readonly string[] Allowed =
    {
        "steam", "discord", "razer", "synapse", "realtek", "rtkaud", "rthd", "nvidia", "nvcontainer", "faceit", "securityhealth", "windows security",
    };

    public static bool IsAllowed(string name, string command) =>
        Allowed.Any(a => name.Contains(a, StringComparison.OrdinalIgnoreCase) || command.Contains(a, StringComparison.OrdinalIgnoreCase));

    public static bool ShouldDisable(StartupEntry e) => e.Enabled && !IsAllowed(e.Name, e.Command);
}
