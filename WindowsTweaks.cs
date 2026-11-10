using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Ajuste do Windows para jogos: lê o estado atual, aplica e guarda o valor original (em AppSettings.TweakBackup)
/// na primeira vez que é alterado, para "Restaurar originais" desfazer tudo.
/// Só ajustes documentados e reversíveis. Nenhum reduz a segurança: VBS, Integridade de Memória, Secure Boot e o Defender
/// ficam como estão (o FACEIT exige). Nunca mexe em UAC nem firewall.
/// </summary>
public sealed class Tweak : INotifyPropertyChanged
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Effect { get; init; } = "";
    public bool NeedsRestart { get; init; }
    /// <summary>Ler o estado demora (PowerShell, schtasks): não entra no retrato das sessões e a página lê em segundo plano.</summary>
    public bool Slow { get; init; }
    /// <summary>Preenchido nos ajustes que reduzem a segurança: texto do aviso mostrado antes de ligar.</summary>
    public string? Warning { get; init; }
    internal Func<bool?> Read { get; init; } = () => null;
    internal Action<bool, AppSettings> Write { get; init; } = (_, _) => { };
    internal Action<AppSettings> Restore { get; init; } = _ => { };

    private bool _isOn;
    public bool IsOn { get => _isOn; set { _isOn = value; Changed(nameof(IsOn)); } }
    private string _status = "";
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public string Badge => string.Join(" · ", new[] { Warning != null ? "REDUZ SEGURANÇA" : null, NeedsRestart ? "REQUER REINICIAR" : null }.OfType<string>());

    public void Refresh() => IsOn = Read() ?? false;

    private void Changed(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class WindowsTweaks
{
    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public static List<Tweak> All()
    {
        var list = Base();
        list.AddRange(CsProfile.Tweaks());
        return list;
    }

    private static List<Tweak> Base() => new()
    {
        new Tweak
        {
            Id = "power", Title = "Plano de energia: Alto desempenho",
            Description = "Mantém a CPU pronta para subir o clock na hora, sem economia agressiva de energia.",
            Effect = "Menos engasgos e 1% low melhor em alguns jogos. Em notebooks gasta mais bateria.",
            Read = () => ActiveScheme()?.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase),
            Write = (on, s) =>
            {
                if (!s.TweakBackup.ContainsKey("power")) s.TweakBackup["power"] = ActiveScheme();
                if (on)
                {
                    // Alguns PCs vêm sem o plano listado: duplicar o modelo padrão o recria com o mesmo GUID.
                    if (!PowerCfg("/list").Contains(HighPerformance, StringComparison.OrdinalIgnoreCase))
                        PowerCfg($"-duplicatescheme {HighPerformance} {HighPerformance}");
                    PowerCfg($"/setactive {HighPerformance}");
                }
                else if (s.TweakBackup["power"] is string prev && !prev.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase))
                    PowerCfg($"/setactive {prev}");
                else PowerCfg("/setactive 381b4222-f694-41f0-9685-ff5bb260df2e"); // Equilibrado
            },
            Restore = s => { if (s.TweakBackup.GetValueOrDefault("power") is string prev) PowerCfg($"/setactive {prev}"); },
        },
        RegTweak("gamemode", "Modo de Jogo",
            "Dá prioridade ao jogo e evita que o Windows Update instale ou reinicie durante a partida.",
            "Recomendado ligado (é o padrão do Windows 11).",
            Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", on: 1, off: 0, absentMeansOn: true),
        new Tweak
        {
            Id = "gamedvr", Title = "Desativar gravação em segundo plano (Game DVR)",
            Description = "Desliga a captura contínua do Xbox Game Bar, que grava os últimos minutos de jogo o tempo todo.",
            Effect = "Recupera alguns FPS e reduz uso de GPU/disco. Você perde o \"gravar o que aconteceu\" do Game Bar.",
            Read = () => (ReadDword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled") ?? 1) == 0 &&
                         (ReadDword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled") ?? 1) == 0,
            Write = (on, s) =>
            {
                SetDword(s, "gamedvr.a", Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", on ? 0 : 1);
                SetDword(s, "gamedvr.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", on ? 0 : 1);
            },
            Restore = s =>
            {
                RestoreDword(s, "gamedvr.a", Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
                RestoreDword(s, "gamedvr.b", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
            },
        },
        RegTweak("hags", "Agendamento de GPU acelerado por hardware",
            "A própria placa de vídeo gerencia sua fila de trabalho, em vez da CPU.",
            "Necessário para o Frame Generation do DLSS 3/4. Pode reduzir latência. Requer GPU e driver compatíveis.",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", on: 2, off: 1,
            absentMeansOn: false, needsRestart: true),
        new Tweak
        {
            Id = "windowed", Title = "Otimizações para jogos em janela",
            Description = "Faz jogos DirectX 10/11 em janela ou \"sem bordas\" usarem o modelo de apresentação moderno (flip).",
            Effect = "Menor latência e suporte a VRR/G-Sync em janela. Recurso do Windows 11.",
            Read = () => DxSettings().GetValueOrDefault("SwapEffectUpgradeEnable") == "1",
            Write = (on, s) =>
            {
                if (!s.TweakBackup.ContainsKey("windowed"))
                    s.TweakBackup["windowed"] = Registry.CurrentUser.OpenSubKey(DxPath)?.GetValue("DirectXUserGlobalSettings") as string ?? "<ausente>";
                var d = DxSettings();
                d["SwapEffectUpgradeEnable"] = on ? "1" : "0";
                using var k = Registry.CurrentUser.CreateSubKey(DxPath);
                k.SetValue("DirectXUserGlobalSettings", string.Concat(d.Select(kv => $"{kv.Key}={kv.Value};")));
            },
            Restore = s =>
            {
                if (s.TweakBackup.GetValueOrDefault("windowed") is not string prev) return;
                using var k = Registry.CurrentUser.CreateSubKey(DxPath);
                if (prev == "<ausente>") k.DeleteValue("DirectXUserGlobalSettings", false);
                else k.SetValue("DirectXUserGlobalSettings", prev);
            },
        },
        new Tweak
        {
            Id = "mouse", Title = "Desligar aceleração do mouse",
            Description = "Desmarca \"Aprimorar precisão do ponteiro\": o cursor anda sempre a mesma distância para o mesmo movimento.",
            Effect = "Não muda FPS, mas deixa a mira consistente em jogos de tiro.",
            Read = () => ReadString(Registry.CurrentUser, @"Control Panel\Mouse", "MouseSpeed") == "0",
            Write = (on, s) =>
            {
                foreach (var n in new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" })
                    if (!s.TweakBackup.ContainsKey($"mouse.{n}")) s.TweakBackup[$"mouse.{n}"] = ReadString(Registry.CurrentUser, @"Control Panel\Mouse", n);
                var v = on ? new[] { "0", "0", "0" } : new[] { "1", "6", "10" }; // padrão do Windows
                ApplyMouse(v);
            },
            Restore = s =>
            {
                if (!s.TweakBackup.ContainsKey("mouse.MouseSpeed")) return;
                ApplyMouse(new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2" }
                    .Select(n => s.TweakBackup.GetValueOrDefault($"mouse.{n}") ?? "0").ToArray());
            },
        },

        // ---------------- otimização profunda (seguros) ----------------

        RegTweak("powerthrottle", "Desligar Power Throttling",
            "Impede o Windows de jogar processos em segundo plano para núcleos econômicos e clocks baixos (EcoQoS).",
            "Ajuda jogos com launcher, Discord ou OBS junto e CPUs híbridas (Intel 12ª geração+). Em notebook gasta mais bateria.",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", on: 1, off: 0,
            absentMeansOn: false, needsRestart: true),
        new Tweak
        {
            Id = "linkpower", Title = "Sem economia de energia em USB e PCIe (na tomada)",
            Description = "No plano de energia atual, desliga a suspensão seletiva do USB e o ASPM do PCIe (placa de vídeo e SSD NVMe), só quando ligado na tomada.",
            Effect = "Evita travadinhas de mouse/headset USB e o atraso do PCIe ao acordar o link. Gasta alguns watts a mais. Se trocar de plano, aplique de novo.",
            Read = () => PowerIndex(SubUsb, UsbSuspend) == 0 && PowerIndex(SubPcie, Aspm) == 0,
            Write = (on, s) =>
            {
                SetPowerIndex(s, "linkpower.usb", SubUsb, UsbSuspend, on ? 0 : 1);
                SetPowerIndex(s, "linkpower.pcie", SubPcie, Aspm, on ? 0 : 1); // 1 = economia moderada (padrão do Equilibrado)
            },
            Restore = s => { RestorePowerIndex(s, "linkpower.usb", SubUsb, UsbSuspend); RestorePowerIndex(s, "linkpower.pcie", SubPcie, Aspm); },
        },
        new Tweak
        {
            Id = "mmcss", Title = "Prioridade de jogos no agendador multimídia",
            Description = "Ajusta o MMCSS (serviço do Windows que dá prioridade a áudio e jogos): reserva menos CPU para tarefas de fundo e põe a categoria \"Games\" em prioridade alta de CPU, GPU e disco.",
            Effect = "Ganho pequeno, mais em 1% low com muita coisa aberta. Não muda nada em jogos que não se registram no MMCSS.",
            Read = () => ReadDword(Registry.LocalMachine, MmcssPath, "SystemResponsiveness") == 10 &&
                         ReadDword(Registry.LocalMachine, MmcssGames, "GPU Priority") == 8 &&
                         ReadString(Registry.LocalMachine, MmcssGames, "Scheduling Category") == "High",
            Write = (on, s) =>
            {
                if (!on) { RestoreMmcss(s); return; }
                SetDword(s, "mmcss.resp", Registry.LocalMachine, MmcssPath, "SystemResponsiveness", 10); // padrão 20 (%)
                SetDword(s, "mmcss.gpu", Registry.LocalMachine, MmcssGames, "GPU Priority", 8);
                SetDword(s, "mmcss.prio", Registry.LocalMachine, MmcssGames, "Priority", 6);
                SetString(s, "mmcss.cat", Registry.LocalMachine, MmcssGames, "Scheduling Category", "High");
                SetString(s, "mmcss.sfio", Registry.LocalMachine, MmcssGames, "SFIO Priority", "High");
            },
            Restore = RestoreMmcss,
        },
        RegTweak("bgapps", "Bloquear apps da Microsoft Store em segundo plano",
            "Política do Windows que impede apps da Store (Clima, Notícias, Fotos...) de rodar escondidos.",
            "Menos CPU e rede usados por trás do jogo. Apps da Store como WhatsApp e o Teams novo deixam de notificar com a janela fechada.",
            Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", on: 2, off: 0,
            absentMeansOn: false),
        RegTweak("search", "Desativar a indexação do Windows Search",
            "Para o serviço que indexa arquivos no disco (WSearch).",
            "Evita picos de disco e CPU durante o jogo. A busca do Iniciar continua achando apps, mas procurar dentro de arquivos e no Outlook fica lento.",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WSearch", "Start", on: 4, off: 2,
            absentMeansOn: false, needsRestart: true),
    };

    /// <summary>Desfaz tudo que foi alterado pelo app (valores guardados antes da primeira alteração).</summary>
    public static void RestoreAll(IEnumerable<Tweak> tweaks, AppSettings s)
    {
        foreach (var t in tweaks)
            try { t.Restore(s); } catch { }
        s.TweakBackup.Clear();
        s.Save();
    }

    // ------------------------------------------------------------------ registro

    private static Tweak RegTweak(string id, string title, string desc, string effect, RegistryKey hive, string path, string name,
                                  int on, int off, bool absentMeansOn, bool needsRestart = false) => new()
    {
        Id = id, Title = title, Description = desc, Effect = effect, NeedsRestart = needsRestart,
        Read = () => (ReadDword(hive, path, name) is int v) ? v == on : absentMeansOn,
        Write = (enable, s) => SetDword(s, id, hive, path, name, enable ? on : off),
        Restore = s => RestoreDword(s, id, hive, path, name),
    };

    internal static int? ReadDword(RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name) is int v ? v : null;
    }

    internal static string? ReadString(RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name)?.ToString();
    }

    internal static void SetDword(AppSettings s, string backupKey, RegistryKey hive, string path, string name, int value)
    {
        if (!s.TweakBackup.ContainsKey(backupKey))
            s.TweakBackup[backupKey] = ReadDword(hive, path, name)?.ToString() ?? "<ausente>";
        using var k = hive.CreateSubKey(path);
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    internal static void RestoreDword(AppSettings s, string backupKey, RegistryKey hive, string path, string name)
    {
        if (s.TweakBackup.GetValueOrDefault(backupKey) is not string prev) return;
        using var k = hive.CreateSubKey(path);
        if (prev == "<ausente>") k.DeleteValue(name, false);
        else k.SetValue(name, int.Parse(prev), RegistryValueKind.DWord);
    }

    internal static void SetString(AppSettings s, string backupKey, RegistryKey hive, string path, string name, string value)
    {
        if (!s.TweakBackup.ContainsKey(backupKey))
            s.TweakBackup[backupKey] = ReadString(hive, path, name) ?? "<ausente>";
        using var k = hive.CreateSubKey(path);
        k.SetValue(name, value, RegistryValueKind.String);
    }

    internal static void RestoreString(AppSettings s, string backupKey, RegistryKey hive, string path, string name)
    {
        if (s.TweakBackup.GetValueOrDefault(backupKey) is not string prev) return;
        using var k = hive.CreateSubKey(path);
        if (prev == "<ausente>") k.DeleteValue(name, false);
        else k.SetValue(name, prev, RegistryValueKind.String);
    }

    private const string DxPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>"Chave=Valor;Chave=Valor;" → dicionário (o valor pode ter outras opções do usuário, que preservamos).</summary>
    private static Dictionary<string, string> DxSettings()
    {
        var d = new Dictionary<string, string>();
        string raw = Registry.CurrentUser.OpenSubKey(DxPath)?.GetValue("DirectXUserGlobalSettings") as string ?? "";
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2) d[kv[0].Trim()] = kv[1].Trim();
        }
        return d;
    }

    private static void ApplyMouse(string[] v)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Mouse"))
        {
            k.SetValue("MouseSpeed", v[0]);
            k.SetValue("MouseThreshold1", v[1]);
            k.SetValue("MouseThreshold2", v[2]);
        }
        // Aplica na hora, sem precisar sair da conta.
        var p = v.Select(int.Parse).ToArray();
        SystemParametersInfo(0x0004 /* SPI_SETMOUSE */, 0, p, 0x01 | 0x02);
    }

    // ------------------------------------------------------------------ otimização profunda

    private const string MmcssPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string MmcssGames = MmcssPath + @"\Tasks\Games";

    private static void RestoreMmcss(AppSettings s)
    {
        RestoreDword(s, "mmcss.resp", Registry.LocalMachine, MmcssPath, "SystemResponsiveness");
        RestoreDword(s, "mmcss.gpu", Registry.LocalMachine, MmcssGames, "GPU Priority");
        RestoreDword(s, "mmcss.prio", Registry.LocalMachine, MmcssGames, "Priority");
        RestoreString(s, "mmcss.cat", Registry.LocalMachine, MmcssGames, "Scheduling Category");
        RestoreString(s, "mmcss.sfio", Registry.LocalMachine, MmcssGames, "SFIO Priority");
    }

    // ---- configurações do plano de energia atual (só na tomada: índice AC) ----

    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3", UsbSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string SubPcie = "501a4d13-42af-4429-9fd1-a8218c268e20", Aspm = "ee12f906-d277-404b-b6da-e5fa1a576df5";

    /// <summary>Índice AC atual. A saída do powercfg é traduzida, mas o primeiro número 0x... é sempre o valor AC.</summary>
    internal static int? PowerIndex(string sub, string setting)
    {
        var m = System.Text.RegularExpressions.Regex.Match(PowerCfg($"/q SCHEME_CURRENT {sub} {setting}"), @"0x([0-9a-fA-F]{8})");
        return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : null;
    }

    internal static void SetPowerIndex(AppSettings s, string backupKey, string sub, string setting, int value)
    {
        if (!s.TweakBackup.ContainsKey(backupKey) && ActiveScheme() is string scheme && PowerIndex(sub, setting) is int cur)
            s.TweakBackup[backupKey] = $"{scheme}|{cur}";
        PowerCfg($"/setacvalueindex SCHEME_CURRENT {sub} {setting} {value}");
        PowerCfg("/setactive SCHEME_CURRENT"); // aplica na hora
    }

    internal static void RestorePowerIndex(AppSettings s, string backupKey, string sub, string setting)
    {
        if (s.TweakBackup.GetValueOrDefault(backupKey)?.Split('|') is not [var scheme, var value]) return;
        PowerCfg($"/setacvalueindex {scheme} {sub} {setting} {value}");
        PowerCfg("/setactive SCHEME_CURRENT");
    }

    // ------------------------------------------------------------------ plano de energia

    /// <summary>Nome do plano de energia ativo, como o Windows mostra ("Equilibrado", "Alto desempenho"...).</summary>
    internal static string? ActiveSchemeName()
    {
        var m = System.Text.RegularExpressions.Regex.Match(PowerCfg("/getactivescheme"), @"\(([^)]+)\)\s*$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    internal static string? ActiveScheme()
    {
        var m = System.Text.RegularExpressions.Regex.Match(PowerCfg("/getactivescheme"), @"[0-9a-fA-F\-]{36}");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    internal static string PowerCfg(string args)
    {
        var psi = new ProcessStartInfo("powercfg.exe", args)
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return output;
    }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, int[] data, uint winIni);
}
