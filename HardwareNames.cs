namespace ResetExplode;

/// <summary>Nomes curtos de hardware para a tela: "RTX 3060", "Ryzen 7 5700X".</summary>
public static class HardwareNames
{
    public static string ShortGpuName(string name)
    {
        string s = name.Replace("(TM)", "").Replace("(R)", "");
        foreach (var w in new[] { "NVIDIA", "GeForce", "AMD", "Intel" }) s = s.Replace(w, "");
        if (s.Contains("Radeon RX")) s = s.Replace("Radeon", "");             // "Radeon RX 9070 XT" → "RX 9070 XT"
        s = string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (s.StartsWith("Arc") && s.EndsWith(" Graphics")) s = s[..^9];        // "Arc B580 Graphics" → "Arc B580"
        return s;
    }

    public static string ShortCpuName(string name)
    {
        string s = name.Replace("AMD", "").Replace("Intel(R)", "").Replace("(R)", "").Replace("(TM)", "").Replace("Core(TM)", "Core");
        int i = s.IndexOf("-Core", StringComparison.OrdinalIgnoreCase);
        if (i > 0) s = s[..s.LastIndexOf(' ', i)];       // "Ryzen 7 7700X 8-Core Processor" → "Ryzen 7 7700X"
        s = s.Replace("Processor", "").Replace("CPU", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\d+th Gen\s*", "");
        int at = s.IndexOf('@');
        if (at > 0) s = s[..at];
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
