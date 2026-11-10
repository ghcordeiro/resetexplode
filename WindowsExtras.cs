using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Ajustes de Windows do catálogo (docs/catalogo-otimizacao.md): Fase 1 (VALE/LIMPEZA, entram no APLICAR PC DE CS) e Fase 2
/// (experimentos para medir na matriz, fora do APLICAR). O valor original de cada item é gravado em disco antes da mudança.
/// </summary>
public static class WindowsExtras
{
    /// <summary>Recursos opcionais que só servem a máquinas virtuais e ao WSL. O Hyper-V completo NÃO entra: pode derrubar o VBS que a FACEIT exige.</summary>
    public static readonly string[] VmFeatures = { "VirtualMachinePlatform", "HypervisorPlatform", "Containers-DisposableClientVM", "Microsoft-Windows-Subsystem-Linux" };

    private const string NicClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    public static List<Tweak> Phase1() => new()
    {
        VmTweak(),
        DefenderTweak(),
        Dword("cs.faststartup", "Inicialização rápida do Windows desligada",
            "Cada \"desligar\" desliga de verdade (drivers e kernel recomeçam do zero), em vez de hibernar o sistema.",
            "Tira estados velhos de driver, causa comum de travadas depois de dias ligado. O boot leva alguns segundos a mais.",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0, restart: true),
        Dword("cs.dosvc", "Atualizações só da rede local (sem enviar para a internet)",
            "A Otimização de Entrega deixa de usar seu upload para distribuir atualizações a outros PCs.",
            "Menos rede e disco em segundo plano.",
            Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0),
        Multi("cs.edge", "Edge sem inicialização acelerada nem execução em segundo plano",
            "O Edge deixa de ficar carregado na memória sem estar aberto.", "Libera RAM e processos.",
            new (RegistryKey, string, string, int)[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", 0),
                (Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", 0),
            }, false),
        Dword("cs.websearch", "Iniciar sem pesquisa na web (Bing)", "A busca do Iniciar procura só no PC.", "Menos consulta de rede e processos de busca.",
            Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
        Dword("cs.storeupdate", "Store sem atualização automática de apps", "Os apps da Store só atualizam quando você mandar.", "Sem download e instalação em segundo plano.",
            Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", 2),
        Dword("cs.telemetrylevel", "Telemetria no nível mínimo permitido", "Política de coleta de diagnóstico no mínimo (no Windows Home o mínimo efetivo é \"Necessário\").",
            "Menos envio de dados e processamento.", Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0),
        MaintenanceTweak(),
    };

    public static List<Tweak> Phase2() => new()
    {
        Dword("exp.timer", "Experimento: timer global do Windows 11 (GlobalTimerResolutionRequests)",
            "Devolve ao sistema inteiro o comportamento antigo do timer: um pedido de resolução fina de um programa vale para todos.",
            "Há relatos dos dois lados e nenhum teste controlado: meça como passo da matriz. Reinicie para valer.",
            Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1, restart: true),
        Dword("exp.mpo", "Experimento: Multiplane Overlay (MPO) desligado",
            "A NVIDIA chegou a recomendar desligar o MPO contra cintilação e travadas com vários monitores e janelas. O Windows novo pode ignorar a chave.",
            "Pode estabilizar o frametime com dois monitores. Meça como passo da matriz. Reinicie para valer.",
            Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5, restart: true),
        NicModeration(),
    };

    // ------------------------------------------------------------------ helpers de registro

    private static Tweak Dword(string id, string title, string desc, string effect, RegistryKey hive, string path, string name, int value, bool restart = false) =>
        Multi(id, title, desc, effect, new[] { (hive, path, name, value) }, restart);

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

    // ------------------------------------------------------------------ recursos de máquina virtual

    private static Tweak VmTweak() => new()
    {
        Id = "cs.vmp", Slow = true, Title = "Plataforma de Máquina Virtual, Sandbox e WSL desligados",
        Description = "Desliga " + string.Join(", ", VmFeatures) + ". A Microsoft diz que a Plataforma de Máquina Virtual pode pesar em jogos e que desligar não afeta a segurança. " +
                      "Você perde WSL2, apps Android e máquinas virtuais. Não desliga o Hyper-V completo nem o VBS (a FACEIT exige).",
        Effect = "Menos camada de virtualização por baixo do jogo. Reinicie para valer e confira na Segurança do Windows que a Integridade de Memória continua ligada.",
        NeedsRestart = true,
        Read = () => { var on = EnabledFeatures(); return on != null && on.Count == 0 && AnyFeatureKnown(); },
        Write = (on, s) =>
        {
            if (!on) { RestoreVm(s); return; }
            var enabled = EnabledFeatures() ?? throw new InvalidOperationException("não consegui ler os recursos do Windows (precisa de administrador)");
            if (enabled.Count == 0) return;
            s.TweakBackup["cs.vmp"] = string.Join(',', enabled.Union((s.TweakBackup.GetValueOrDefault("cs.vmp") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)));
            s.Save();
            foreach (var f in enabled)
                Ps($"Disable-WindowsOptionalFeature -Online -FeatureName '{f}' -NoRestart -ErrorAction Stop | Out-Null", 300000);
        },
        Restore = RestoreVm,
    };

    private static void RestoreVm(AppSettings s)
    {
        foreach (var f in (s.TweakBackup.GetValueOrDefault("cs.vmp") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Where(f => VmFeatures.Contains(f)))
            Ps($"Enable-WindowsOptionalFeature -Online -FeatureName '{f}' -NoRestart -ErrorAction Stop | Out-Null", 300000);
    }

    /// <summary>Recursos da lista que estão ligados, ou <c>null</c> se o PowerShell falhou.</summary>
    private static List<string>? EnabledFeatures()
    {
        string names = string.Join(",", VmFeatures.Select(f => $"'{f}'"));
        string o = Ps($"Get-WindowsOptionalFeature -Online | Where-Object {{ $_.State -eq 'Enabled' -and $_.FeatureName -in @({names}) }} | ForEach-Object {{ $_.FeatureName }}", 120000, out bool ok);
        return ok ? o.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => VmFeatures.Contains(x)).ToList() : null;
    }

    private static bool AnyFeatureKnown() => true;

    // ------------------------------------------------------------------ Defender: verificação leve e fora do horário de jogo

    private static Tweak DefenderTweak() => new()
    {
        Id = "cs.defender", Slow = true, Title = "Defender: verificação com no máximo 25% de CPU, às 4h",
        Description = "A proteção em tempo real continua igual. Só a verificação agendada passa a usar menos CPU (ScanAvgCPULoadFactor 25) e roda às 4h, mesmo com o PC em uso.",
        Effect = "Evita uma verificação pesada no meio da partida. Antivírus de outra marca: sem efeito.",
        Read = () => DefenderState() is { } d && d.Factor == 25 && !d.IdleOnly && d.Time == "04:00:00",
        Write = (on, s) =>
        {
            if (!on) { RestoreDefender(s); return; }
            var cur = DefenderState() ?? throw new InvalidOperationException("o Defender não respondeu (antivírus de outra marca?)");
            if (!s.TweakBackup.ContainsKey("cs.defender")) { s.TweakBackup["cs.defender"] = $"{cur.Factor}|{cur.IdleOnly}|{cur.Time}"; s.Save(); }
            Ps("Set-MpPreference -ScanAvgCPULoadFactor 25 -ScanOnlyIfIdle $false -ScanScheduleTime 04:00:00 -ErrorAction Stop", 60000);
        },
        Restore = RestoreDefender,
    };

    private static void RestoreDefender(AppSettings s)
    {
        if (s.TweakBackup.GetValueOrDefault("cs.defender")?.Split('|') is not [var f, var idle, var t]) return;
        if (!int.TryParse(f, out int factor) || !TimeSpan.TryParse(t, out _)) return;
        Ps($"Set-MpPreference -ScanAvgCPULoadFactor {factor} -ScanOnlyIfIdle ${idle} -ScanScheduleTime {t} -ErrorAction Stop", 60000);
    }

    private sealed record DefState(int Factor, bool IdleOnly, string Time);

    private static DefState? DefenderState()
    {
        string o = Ps("$p = Get-MpPreference; \"$($p.ScanAvgCPULoadFactor)|$($p.ScanOnlyIfIdleEnabled)|$($p.ScanScheduleTime.ToString('hh\\:mm\\:ss'))\"", 60000, out bool ok).Trim();
        var parts = o.Split('|');
        return ok && parts.Length == 3 && int.TryParse(parts[0], out int f) ? new DefState(f, parts[1].Equals("True", StringComparison.OrdinalIgnoreCase), parts[2]) : null;
    }

    // ------------------------------------------------------------------ manutenção automática às 3h

    private const string MaintPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance";

    private static Tweak MaintenanceTweak() => new()
    {
        Id = "cs.maintenance", Title = "Manutenção automática do Windows às 3h",
        Description = "A manutenção (otimização de disco, atualizações de apps, verificações) começa às 3h em vez de em qualquer hora ociosa.",
        Effect = "Evita a manutenção começar no meio da partida.",
        Read = () => (WindowsTweaks.ReadString(Registry.LocalMachine, MaintPath, "Activation Boundary") ?? "").Contains("T03:00:00"),
        Write = (on, s) =>
        {
            if (!on) { WindowsTweaks.RestoreString(s, "cs.maintenance", Registry.LocalMachine, MaintPath, "Activation Boundary"); return; }
            if (!s.TweakBackup.ContainsKey("cs.maintenance")) { s.TweakBackup["cs.maintenance"] = WindowsTweaks.ReadString(Registry.LocalMachine, MaintPath, "Activation Boundary") ?? "<ausente>"; s.Save(); }
            WindowsTweaks.SetString(s, "cs.maintenance", Registry.LocalMachine, MaintPath, "Activation Boundary", "2001-01-01T03:00:00");
        },
        Restore = s => WindowsTweaks.RestoreString(s, "cs.maintenance", Registry.LocalMachine, MaintPath, "Activation Boundary"),
    };

    // ------------------------------------------------------------------ moderação de interrupções da placa de rede (experimento)

    private static IEnumerable<string> NicsWithModeration()
    {
        using var root = Registry.LocalMachine.OpenSubKey(NicClass);
        if (root == null) yield break;
        foreach (var name in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("*InterruptModeration") != null && k.GetValue("Characteristics") is int c && (c & 0x4) != 0) yield return name;
        }
    }

    private static Tweak NicModeration() => new()
    {
        Id = "exp.nicmod", Title = "Experimento: moderação de interrupções da placa de rede desligada",
        Description = "A placa passa a interromper a CPU a cada pacote, em vez de agrupar. A Microsoft documenta que reduz a latência e gasta mais CPU. Meça com o diagnóstico de DPC.",
        Effect = "Pode reduzir picos de ping; em outros PCs só gasta CPU. Reinicie ou reative o adaptador para valer.",
        NeedsRestart = true,
        Read = () => { var n = NicsWithModeration().ToList(); return n.Count > 0 && n.All(x => WindowsTweaks.ReadString(Registry.LocalMachine, $@"{NicClass}\{x}", "*InterruptModeration") == "0"); },
        Write = (on, s) =>
        {
            if (!on) { RestoreNic(s); return; }
            var nics = NicsWithModeration().ToList();
            foreach (var n in nics)
                if (!s.TweakBackup.ContainsKey($"exp.nicmod.{n}"))
                    s.TweakBackup[$"exp.nicmod.{n}"] = WindowsTweaks.ReadString(Registry.LocalMachine, $@"{NicClass}\{n}", "*InterruptModeration") ?? "<ausente>";
            s.Save();
            foreach (var n in nics) WindowsTweaks.SetString(s, $"exp.nicmod.{n}", Registry.LocalMachine, $@"{NicClass}\{n}", "*InterruptModeration", "0");
        },
        Restore = RestoreNic,
    };

    private static void RestoreNic(AppSettings s)
    {
        foreach (var n in NicsWithModeration())
            WindowsTweaks.RestoreString(s, $"exp.nicmod.{n}", Registry.LocalMachine, $@"{NicClass}\{n}", "*InterruptModeration");
    }

    // ------------------------------------------------------------------ PowerShell

    internal static string Ps(string command, int timeoutMs) => Ps(command, timeoutMs, out _);

    internal static string Ps(string command, int timeoutMs, out bool ok)
    {
        ok = false;
        try
        {
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -")
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi)!;
            p.StandardInput.WriteLine(command);
            p.StandardInput.Close();
            var err = p.StandardError.ReadToEndAsync();
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return o; }
            ok = p.ExitCode == 0;
            if (!ok && err.Result.Trim().Length > 0) throw new InvalidOperationException(err.Result.Trim().Split('\n')[0].Trim());
            return o;
        }
        catch (InvalidOperationException) { throw; }
        catch { return ""; }
    }
}
