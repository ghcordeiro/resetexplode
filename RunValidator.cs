namespace ResetExplode;

/// <summary>O que se sabe de uma execução ao terminar, para decidir se ela vale para comparação.</summary>
public sealed record RunFacts(long Frames, double Seconds, bool LostFocus, bool ContextChanged, bool PresentMonLostEvents, bool AntiCheatSeen);

public static class RunValidator
{
    public const long MinFrames = 100;
    public const double MinSeconds = 5;

    /// <summary><c>null</c> se a execução é válida; senão o motivo, em português.</summary>
    public static string? Validate(RunFacts f)
    {
        var reasons = new List<string>();
        if (f.Frames < MinFrames || f.Seconds < MinSeconds) reasons.Add("poucos quadros");
        if (f.LostFocus) reasons.Add("alt-tab durante a captura");
        if (f.ContextChanged) reasons.Add("configuração mudou durante a captura");
        if (f.PresentMonLostEvents) reasons.Add("PresentMon não gravou dados");
        if (f.AntiCheatSeen) reasons.Add("FACEIT em execução");
        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }
}
