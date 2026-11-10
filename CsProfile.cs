using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Perfil "PC de CS": deixa o Windows dedicado ao CS2 desativando o que ele não usa. Tudo reversível: o valor original de cada item é
/// gravado em disco ANTES da mudança (TweakBackup) e RESTAURAR ORIGINAIS devolve. Nunca toca em áudio, rede (DHCP/DNS), Defender,
/// Windows Update (só o horário ativo), TPM, Secure Boot nem VBS. Serviços são desativados, nunca apagados.
/// </summary>
public static class CsProfile
{
    /// <summary>Serviços que o CS2 e o dia a dia deste PC não usam (Bluetooth, impressão, telemetria, Xbox, mapas...). Item que não existe no Windows é pulado.</summary>
    public static readonly IReadOnlyList<string> ServicesToDisable = new[]
    {
        "SysMain", "Spooler", "Fax", "DiagTrack", "dmwappushservice", "MapsBroker", "XblAuthManager", "XblGameSave", "XboxGipSvc",
        "XboxNetApiSvc", "WerSvc", "RetailDemo", "PhoneSvc", "lfsvc", "RemoteRegistry", "BthAvctpSvc", "bthserv",
    };

    /// <summary>Nunca desativar, mesmo que alguém acrescente por engano a lista acima: rede, áudio, segurança, atualização, discos, Steam, Razer e NVIDIA.</summary>
    public static readonly IReadOnlyList<string> NeverTouch = new[]
    {
        "Dhcp", "Dnscache", "NlaSvc", "netprofm", "Audiosrv", "AudioEndpointBuilder", "WinDefend", "wuauserv", "UsoSvc", "BITS", "TrustedInstaller",
        "Steam Client Service", "RzSDKService", "NvContainerLocalSystem", "NVDisplay.ContainerLocalSystem", "StorSvc", "vds", "PlugPlay", "EventLog",
        "RpcSs", "DcomLaunch", "BFE", "mpssvc", "Winmgmt", "LanmanWorkstation", "SecurityHealthService", "TPM", "tpm",
    };

    private const string Svc = @"SYSTEM\CurrentControlSet\Services\";
    private const string PowerProcSub = "54533251-82be-4824-96c1-47b60b740d00", PowerMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string NicClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static readonly string[] TaskPaths =
    {
        @"\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser",
        @"\Microsoft\Windows\Application Experience\ProgramDataUpdater",
        @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator",
        @"\Microsoft\Windows\Customer Experience Improvement Program\UsbCeip",
    };

    /// <summary>Ajustes do Windows que ficam fora do APLICAR TUDO por serem experimentos a medir no benchmark, não ganhos garantidos.</summary>
    public static readonly string[] ExperimentOnly = { "hags", "exp.dynamictick", "exp.prio" };

    static CsProfile()
    {
        var clash = ServicesToDisable.Intersect(NeverTouch, StringComparer.OrdinalIgnoreCase).ToList();
        if (clash.Count > 0) throw new InvalidOperationException("Serviço proibido na lista de desativação: " + string.Join(", ", clash));
    }

    public static List<Tweak> Tweaks() => new()
    {
        ServicesTweak(),
        Multi("cs.update", "Windows Update: horário ativo máximo e sem reinício com você logado",
            "Mantém as atualizações ligadas, mas o Windows não reinicia sozinho enquanto há alguém logado e o horário ativo vai ao máximo (18 h).",
            "Evita reinício ou instalação no meio do jogo. O Windows Update continua funcionando.",
            new (RegistryKey, string, string, int)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "ActiveHoursStart", 10),
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", "ActiveHoursEnd", 4),
                (Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoRebootWithLoggedOnUsers", 1),
            }, restart: false),
        Multi("cs.shell", "Widgets, Copilot, sugestões e dicas desligados",
            "Desliga os Widgets e o Copilot e as sugestões/apps promovidos do Windows.",
            "Menos processos e consultas de rede em segundo plano.",
            new (RegistryKey, string, string, int)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0),
                (Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-310093Enabled", 0),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "PreInstalledAppsEnabled", 0),
            }, restart: true),
        Multi("cs.gamebar", "Xbox Game Bar desligado",
            "Desliga a Xbox Game Bar (Win+G) e o painel de inicialização dela.",
            "Um overlay a menos por cima do jogo. A gravação em segundo plano já é desligada no ajuste Game DVR.",
            new (RegistryKey, string, string, int)[]
            {
                (Registry.CurrentUser, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0),
                (Registry.CurrentUser, @"Software\Microsoft\GameBar", "ShowStartupPanel", 0),
            }, restart: false),
        Multi("cs.visual", "Efeitos visuais do Windows desligados (transparência e animações da barra)",
            "Tira a transparência e as animações da barra de tarefas.",
            "Pequeno alívio para a GPU e o compositor do Windows, que também desenha por cima do jogo em janela.",
            new (RegistryKey, string, string, int)[]
            {
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0),
            }, restart: false),
        new Tweak
        {
            Id = "cs.parking", Title = "Núcleos da CPU sempre ativos (sem estacionamento)",
            Description = "No plano de energia atual, o mínimo de núcleos ativos fica em 100% (na tomada).",
            Effect = "Evita o tempo que um núcleo estacionado leva para acordar: pode reduzir quedas isoladas.",
            Read = () => WindowsTweaks.PowerIndex(PowerProcSub, PowerMinCores) == 100,
            Write = (on, s) => { if (on) WindowsTweaks.SetPowerIndex(s, "cs.parking", PowerProcSub, PowerMinCores, 100); else WindowsTweaks.RestorePowerIndex(s, "cs.parking", PowerProcSub, PowerMinCores); },
            Restore = s => WindowsTweaks.RestorePowerIndex(s, "cs.parking", PowerProcSub, PowerMinCores),
        },
        NicTweak(),
        TasksTweak(),
        StartupTweak(),
        SystemTimers.DynamicTick(),
        SystemTimers.PrioritySeparation(),
    };

    // ------------------------------------------------------------------ itens

    private static Tweak ServicesTweak() => new()
    {
        Id = "cs.services", Title = "Serviços que o CS não usa (impressão, telemetria, Xbox, mapas, Bluetooth…)",
        Description = "Desativa " + string.Join(", ", ServicesToDisable) + ". Os que o seu Windows não tem são ignorados. Nada é apagado.",
        Effect = "Menos serviços e consultas de rede em segundo plano. Sem impressão e sem Bluetooth até restaurar.",
        NeedsRestart = true,
        Read = () =>
        {
            var present = ServicesToDisable.Where(Exists).ToList();
            return present.Count > 0 && present.All(n => WindowsTweaks.ReadDword(Registry.LocalMachine, Svc + n, "Start") == 4);
        },
        Write = (on, s) =>
        {
            if (!on) { RestoreServices(s); return; }
            var present = ServicesToDisable.Where(Exists).Where(n => !NeverTouch.Contains(n, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var n in present) // guarda o original em disco ANTES de mudar qualquer um
                if (!s.TweakBackup.ContainsKey("cs.svc." + n))
                    s.TweakBackup["cs.svc." + n] = WindowsTweaks.ReadDword(Registry.LocalMachine, Svc + n, "Start")?.ToString() ?? "<ausente>";
            s.Save();
            foreach (var n in present)
            {
                WindowsTweaks.SetDword(s, "cs.svc." + n, Registry.LocalMachine, Svc + n, "Start", 4);
                try { Process.Start(new ProcessStartInfo("sc.exe", $"stop \"{n}\"") { CreateNoWindow = true, UseShellExecute = false })?.Dispose(); } catch { }
            }
        },
        Restore = RestoreServices,
    };

    private static void RestoreServices(AppSettings s)
    {
        foreach (var n in ServicesToDisable)
            WindowsTweaks.RestoreDword(s, "cs.svc." + n, Registry.LocalMachine, Svc + n, "Start");
    }

    private static bool Exists(string service)
    {
        using var k = Registry.LocalMachine.OpenSubKey(Svc + service);
        return k != null;
    }

    /// <summary>Vários valores DWORD de registro como um só ajuste, com backup de cada um antes de mudar.</summary>
    private static Tweak Multi(string id, string title, string desc, string effect, (RegistryKey Hive, string Path, string Name, int Value)[] values, bool restart)
    {
        string Key(int i) => $"{id}.{i}";
        void Restore(AppSettings s) { for (int i = 0; i < values.Length; i++) WindowsTweaks.RestoreDword(s, Key(i), values[i].Hive, values[i].Path, values[i].Name); }
        return new Tweak
        {
            Id = id, Title = title, Description = desc, Effect = effect, NeedsRestart = restart,
            Read = () => values.All(v => WindowsTweaks.ReadDword(v.Hive, v.Path, v.Name) == v.Value),
            Write = (on, s) =>
            {
                if (!on) { Restore(s); return; }
                for (int i = 0; i < values.Length; i++)
                    if (!s.TweakBackup.ContainsKey(Key(i)))
                        s.TweakBackup[Key(i)] = WindowsTweaks.ReadDword(values[i].Hive, values[i].Path, values[i].Name)?.ToString() ?? "<ausente>";
                s.Save();
                for (int i = 0; i < values.Length; i++)
                    WindowsTweaks.SetDword(s, Key(i), values[i].Hive, values[i].Path, values[i].Name, values[i].Value);
            },
            Restore = Restore,
        };
    }

    // ---- placa de rede física: sem economia de energia ----

    private static IEnumerable<string> PhysicalNics()
    {
        using var root = Registry.LocalMachine.OpenSubKey(NicClass);
        if (root == null) yield break;
        foreach (var name in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var k = root.OpenSubKey(name);
            if (k == null) continue;
            bool physical = k.GetValue("Characteristics") is int c && (c & 0x4) != 0; // NCF_PHYSICAL
            string ifType = k.GetValue("*IfType")?.ToString() ?? "";
            if (physical && ifType is "6" or "71") yield return name;
        }
    }

    private static Tweak NicTweak() => new()
    {
        Id = "cs.nic", Title = "Placa de rede sem economia de energia",
        Description = "Na placa de rede física (cabo e Wi-Fi), desmarca \"permitir que o computador desligue este dispositivo\" e desliga o Ethernet de Eficiência Energética.",
        Effect = "Evita a placa dormir e acordar no meio da partida (picos de ping). Vale depois de reiniciar o PC.",
        NeedsRestart = true,
        Read = () => { var n = PhysicalNics().ToList(); return n.Count > 0 && n.All(x => WindowsTweaks.ReadDword(Registry.LocalMachine, $@"{NicClass}\{x}", "PnPCapabilities") == 24); },
        Write = (on, s) =>
        {
            if (!on) { RestoreNics(s); return; }
            var nics = PhysicalNics().ToList();
            foreach (var n in nics)
            {
                string path = $@"{NicClass}\{n}";
                if (!s.TweakBackup.ContainsKey($"cs.nic.{n}.pnp")) s.TweakBackup[$"cs.nic.{n}.pnp"] = WindowsTweaks.ReadDword(Registry.LocalMachine, path, "PnPCapabilities")?.ToString() ?? "<ausente>";
                if (WindowsTweaks.ReadString(Registry.LocalMachine, path, "*EEE") != null && !s.TweakBackup.ContainsKey($"cs.nic.{n}.eee"))
                    s.TweakBackup[$"cs.nic.{n}.eee"] = WindowsTweaks.ReadString(Registry.LocalMachine, path, "*EEE")!;
            }
            s.Save();
            foreach (var n in nics)
            {
                string path = $@"{NicClass}\{n}";
                WindowsTweaks.SetDword(s, $"cs.nic.{n}.pnp", Registry.LocalMachine, path, "PnPCapabilities", 24);
                if (s.TweakBackup.ContainsKey($"cs.nic.{n}.eee")) WindowsTweaks.SetString(s, $"cs.nic.{n}.eee", Registry.LocalMachine, path, "*EEE", "0");
            }
        },
        Restore = RestoreNics,
    };

    private static void RestoreNics(AppSettings s)
    {
        foreach (var n in PhysicalNics())
        {
            string path = $@"{NicClass}\{n}";
            WindowsTweaks.RestoreDword(s, $"cs.nic.{n}.pnp", Registry.LocalMachine, path, "PnPCapabilities");
            WindowsTweaks.RestoreString(s, $"cs.nic.{n}.eee", Registry.LocalMachine, path, "*EEE");
        }
    }

    // ---- tarefas agendadas de telemetria ----

    private static string SchTasks(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return p.ExitCode == 0 ? o : "";
        }
        catch { return ""; }
    }

    /// <summary><c>null</c> se a tarefa não existe; senão se está habilitada (o XML só traz &lt;Enabled&gt;false&lt;/Enabled&gt; quando desligada).</summary>
    private static bool? TaskEnabled(string path)
    {
        string xml = SchTasks($"/Query /TN \"{path}\" /XML");
        return xml.Contains("<Task") ? !xml.Contains("<Enabled>false</Enabled>") : null;
    }

    private static Tweak TasksTweak() => new()
    {
        Id = "cs.tasks", Title = "Tarefas de telemetria do Windows desativadas",
        Description = "Desativa o Compatibility Appraiser, o ProgramDataUpdater e as tarefas do Programa de Melhoria da Experiência.",
        Effect = "Evita varreduras de disco e CPU que o Windows agenda sozinho.",
        Read = () => { var t = TaskPaths.Select(TaskEnabled).Where(x => x != null).ToList(); return t.Count > 0 && t.All(x => x == false); },
        Write = (on, s) =>
        {
            if (!on) { RestoreTasks(s); return; }
            var enabled = TaskPaths.Select((p, i) => (p, i)).Where(x => TaskEnabled(x.p) == true).ToList();
            foreach (var (_, i) in enabled) s.TweakBackup[$"cs.task.{i}"] = "1";
            s.Save();
            foreach (var (p, _) in enabled) SchTasks($"/Change /TN \"{p}\" /Disable");
        },
        Restore = RestoreTasks,
    };

    private static void RestoreTasks(AppSettings s)
    {
        for (int i = 0; i < TaskPaths.Length; i++)
            if (s.TweakBackup.GetValueOrDefault($"cs.task.{i}") == "1") SchTasks($"/Change /TN \"{TaskPaths[i]}\" /Enable");
    }

    // ---- inicialização ----

    private static string EntryKey(StartupEntry e) => e.Source + "\t" + e.Name;

    private static Tweak StartupTweak() => new()
    {
        Id = "cs.startup", Title = "Inicialização: só o essencial (Steam, Discord, Razer, áudio, NVIDIA, FACEIT)",
        Description = "Desativa os demais programas que abrem com o Windows, sem apagar nada (o mesmo interruptor do Gerenciador de Tarefas).",
        Effect = "Menos programas disputando CPU, disco e rede depois de ligar o PC.",
        Read = () => StartupManager.List().All(e => !StartupPolicy.ShouldDisable(e)),
        Write = (on, s) =>
        {
            if (!on) { RestoreStartup(s); return; }
            var todo = StartupManager.List().Where(StartupPolicy.ShouldDisable).ToList();
            var keys = (s.TweakBackup.GetValueOrDefault("cs.startup") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Union(todo.Select(EntryKey)).ToList();
            s.TweakBackup["cs.startup"] = string.Join('\n', keys);
            s.Save();
            foreach (var e in todo) { try { StartupManager.SetEnabled(e, false); } catch { } }
        },
        Restore = RestoreStartup,
    };

    private static void RestoreStartup(AppSettings s)
    {
        var keys = (s.TweakBackup.GetValueOrDefault("cs.startup") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (keys.Count == 0) return;
        foreach (var e in StartupManager.List().Where(e => keys.Contains(EntryKey(e))))
            try { StartupManager.SetEnabled(e, true); } catch { }
    }

    // ------------------------------------------------------------------ aplicar tudo

    /// <summary>Cria um ponto de restauração do Windows. O Windows só cria um por 24 h por padrão e exige a Proteção do Sistema ligada.</summary>
    public static (bool Ok, string Message) CreateRestorePoint()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"Checkpoint-Computer -Description 'RESET EXPLODE - PC de CS' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop\"")
            { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return (false, "demorou demais"); }
            return p.ExitCode == 0 ? (true, "ponto de restauração criado") : (false, err.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "o Windows recusou");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Liga todos os ajustes seguros que ainda estão desligados, gravando o original de cada um em disco antes. Devolve o que foi ligado e o que falhou.</summary>
    public static (List<string> Applied, List<string> Failed) ApplyAll(IEnumerable<Tweak> tweaks, AppSettings s)
    {
        var applied = new List<string>(); var failed = new List<string>();
        foreach (var t in tweaks.Where(t => t.Warning == null && !ExperimentOnly.Contains(t.Id)))
        {
            try
            {
                t.Refresh();
                if (t.IsOn) continue;
                t.Write(true, s);
                s.Save();
                t.Refresh();
                (t.IsOn ? applied : failed).Add(t.Title);
            }
            catch (Exception ex) { failed.Add($"{t.Title} ({ex.Message})"); }
        }
        return (applied, failed);
    }
}
