using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ResetExplode;

public sealed record OverlayItem(string Id, string Title, string State, string Advice, bool CanFix, bool CanRestore);

/// <summary>
/// Overlays e aceleração de hardware dos programas que ficam abertos junto com o jogo. O app só altera o que tem arquivo de
/// configuração conhecido (aceleração do Discord, com backup); o resto ele detecta e explica onde desligar.
/// </summary>
public static class OverlayCheck
{
    private static string DiscordSettings => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "discord", "settings.json");
    private static string DiscordBackup => Path.Combine(AppSettings.Folder, "discord-settings.backup.json");

    public static List<OverlayItem> Items()
    {
        var list = new List<OverlayItem>();
        // Discord: aceleração de hardware (usa a GPU para desenhar o próprio Discord)
        if (File.Exists(DiscordSettings))
        {
            bool? hw = null;
            try { hw = JsonNode.Parse(File.ReadAllText(DiscordSettings))?["enableHardwareAcceleration"]?.GetValue<bool>() ?? true; } catch { }
            list.Add(new("discord_hw", "Discord · aceleração de hardware",
                hw switch { true => "LIGADA", false => "desligada", _ => "não consegui ler" },
                "Ligada, o Discord usa a placa de vídeo junto com o jogo. Desligar não afeta a call. O app muda o arquivo com o Discord fechado e guarda o original.",
                hw == true, File.Exists(DiscordBackup)));
        }
        else list.Add(new("discord_hw", "Discord · aceleração de hardware", "Discord não encontrado", "", false, false));

        list.Add(new("discord_overlay", "Discord · sobreposição no jogo", "confira no Discord",
            "Discord › Configurações › Sobreposição de jogo: desligue, ou desligue só para o CS2 em Atividade registrada. O app não consegue ler essa opção.", false, false));

        list.Add(new("steam_overlay", "Steam · overlay no CS2", "confira na Steam",
            "Steam › Biblioteca › Counter-Strike 2 › Propriedades › Geral: desmarque \"Ativar o overlay da Steam durante o jogo\".", false, false));

        string? nv = new[] { "NVIDIA Overlay", "nvsphelper64", "NVIDIA Share" }.FirstOrDefault(n => Running(n));
        list.Add(new("nvidia_overlay", "NVIDIA · overlay e gravação instantânea", nv != null ? $"LIGADO ({nv} aberto)" : "desligado",
            "NVIDIA App › Configurações › Recursos: desligue a sobreposição do jogo e a gravação instantânea (Instant Replay).", false, false));

        list.Add(new("gamebar", "Xbox Game Bar", "veja \"Xbox Game Bar desligado\" nos ajustes do Windows",
            "O interruptor fica na lista de ajustes acima, com restauração.", false, false));
        return list;
    }

    private static bool Running(string name)
    {
        var p = Process.GetProcessesByName(name);
        try { return p.Length > 0; } finally { foreach (var x in p) x.Dispose(); }
    }

    public static void Fix(string id)
    {
        if (id != "discord_hw") return;
        if (Running("Discord")) throw new InvalidOperationException("Feche o Discord (inclusive o ícone perto do relógio) e tente de novo: ele regrava o arquivo ao fechar.");
        var node = JsonNode.Parse(File.ReadAllText(DiscordSettings))?.AsObject() ?? throw new InvalidOperationException("settings.json do Discord ilegível.");
        Directory.CreateDirectory(AppSettings.Folder);
        if (!File.Exists(DiscordBackup)) File.Copy(DiscordSettings, DiscordBackup);
        node["enableHardwareAcceleration"] = false;
        File.WriteAllText(DiscordSettings, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void Restore(string id)
    {
        if (id != "discord_hw" || !File.Exists(DiscordBackup)) return;
        if (Running("Discord")) throw new InvalidOperationException("Feche o Discord antes de restaurar.");
        File.Copy(DiscordBackup, DiscordSettings, true);
        File.Delete(DiscordBackup);
    }
}
