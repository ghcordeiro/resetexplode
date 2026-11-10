using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ResetExplode;

public sealed class BloatApp : INotifyPropertyChanged
{
    public string Name { get; init; } = "";          // ex.: Microsoft.BingNews
    public string FullName { get; init; } = "";      // PackageFullName
    public string Family { get; init; } = "";        // PackageFamilyName (para reinstalar pela Store)
    public string Title { get; init; } = "";
    private bool _selected = true;
    public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Remove apps da Store que vêm com o Windows e não servem a este PC, só para o usuário atual. Lista fechada: o que não está nela nunca aparece,
/// e a lista "nunca" trava a Store, o winget, a segurança do Windows e as bibliotecas que outros apps usam. Reinstalação é pela Store.
/// </summary>
public static class Debloat
{
    public static readonly Dictionary<string, string> Removable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Clipchamp.Clipchamp"] = "Clipchamp (editor de vídeo)",
        ["Microsoft.BingNews"] = "Notícias",
        ["Microsoft.BingWeather"] = "Clima",
        ["Microsoft.MicrosoftSolitaireCollection"] = "Paciência",
        ["MSTeams"] = "Teams (pessoal)",
        ["MicrosoftTeams"] = "Teams (pessoal, antigo)",
        ["Microsoft.GamingApp"] = "Xbox",
        ["Microsoft.XboxApp"] = "Xbox (antigo)",
        ["Microsoft.GetHelp"] = "Obter Ajuda",
        ["Microsoft.Getstarted"] = "Dicas",
        ["Microsoft.People"] = "Pessoas",
        ["Microsoft.WindowsFeedbackHub"] = "Hub de Comentários",
        ["Microsoft.ZuneMusic"] = "Media Player / Groove",
        ["Microsoft.ZuneVideo"] = "Filmes e TV",
        ["Microsoft.YourPhone"] = "Vincular ao Celular",
        ["Microsoft.MicrosoftOfficeHub"] = "Microsoft 365 (Office)",
        ["Microsoft.Todos"] = "To Do",
        ["Microsoft.PowerAutomateDesktop"] = "Power Automate",
        ["Microsoft.WindowsMaps"] = "Mapas",
        ["Microsoft.OutlookForWindows"] = "Outlook (novo)",
        ["Microsoft.Copilot"] = "Copilot",
        ["Microsoft.549981C3F5F10"] = "Cortana",
        ["Microsoft.BingSearch"] = "Pesquisa do Bing",
        ["Microsoft.Windows.DevHome"] = "Dev Home",
    };

    public static readonly string[] Never =
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI", "Microsoft.VCLibs",
        "Microsoft.UI.Xaml", "Microsoft.NET", "Microsoft.XboxIdentityProvider", "Microsoft.XboxGameCallableUI", "Microsoft.Xbox.TCUI",
        "Microsoft.WindowsCalculator", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.Windows.Photos",
        "Microsoft.WindowsTerminal", "Microsoft.HEVCVideoExtension", "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions",
    };

    static Debloat()
    {
        var clash = Removable.Keys.Where(k => Never.Any(n => k.StartsWith(n, StringComparison.OrdinalIgnoreCase))).ToList();
        if (clash.Count > 0) throw new InvalidOperationException("App protegido na lista de remoção: " + string.Join(", ", clash));
    }

    private static string RemovedPath => Path.Combine(AppSettings.Folder, "debloat-removidos.json");

    /// <summary>Apps da lista que existem neste usuário.</summary>
    public static List<BloatApp> Installed()
    {
        string json = Ps("Get-AppxPackage | Select-Object Name,PackageFullName,PackageFamilyName | ConvertTo-Json -Compress");
        var list = new List<BloatApp>();
        if (json.Trim().Length == 0) return list;
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
        foreach (var e in items)
        {
            string name = e.GetProperty("Name").GetString() ?? "";
            if (!Removable.TryGetValue(name, out var title)) continue;
            if (Never.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new BloatApp { Name = name, Title = title, FullName = e.GetProperty("PackageFullName").GetString() ?? "", Family = e.GetProperty("PackageFamilyName").GetString() ?? "" });
        }
        return list.OrderBy(a => a.Title).ToList();
    }

    /// <summary>Remove os escolhidos (só para este usuário) e guarda a lista para reinstalar pela Store.</summary>
    public static (List<string> Removed, List<string> Failed) Remove(IEnumerable<BloatApp> apps)
    {
        var removed = new List<string>(); var failed = new List<string>();
        var saved = LoadRemoved();
        foreach (var a in apps)
        {
            if (!Removable.ContainsKey(a.Name) || Never.Any(n => a.Name.StartsWith(n, StringComparison.OrdinalIgnoreCase))) continue;
            saved[a.Family] = a.Title; // grava antes de remover
            File.WriteAllText(RemovedPath, JsonSerializer.Serialize(saved));
            string err = Ps($"try {{ Remove-AppxPackage -Package '{a.FullName.Replace("'", "")}' -ErrorAction Stop; 'ok' }} catch {{ $_.Exception.Message }}");
            if (err.Trim() == "ok") removed.Add(a.Title); else failed.Add($"{a.Title} ({err.Trim().Split('\n')[0]})");
        }
        return (removed, failed);
    }

    /// <summary>Família → nome dos apps removidos pelo app (para o botão de reinstalar pela Store).</summary>
    public static Dictionary<string, string> LoadRemoved()
    {
        try { if (File.Exists(RemovedPath)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RemovedPath)) ?? new(); }
        catch { }
        return new();
    }

    public static void OpenInStore(string family) => ShellRunner.OpenUrl($"ms-windows-store://pdp/?PFN={family}");

    private static string Ps(string command)
    {
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "\\\"")}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd();
        p.WaitForExit(120000);
        return o;
    }
}
