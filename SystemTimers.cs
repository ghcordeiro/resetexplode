using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Timer do sistema e interrupções: diagnóstico só de leitura (HPET forçado, dynamic tick, modo MSI da GPU e da rede, BitLocker) e os dois
/// experimentos de sistema com base técnica (disabledynamictick e Win32PrioritySeparation), que ficam fora do APLICAR PC DE CS: são para medir na matriz.
/// </summary>
public static class SystemTimers
{
    private const string PrioPath = @"SYSTEM\CurrentControlSet\Control\PriorityControl";

    /// <summary>BitLocker protegendo o disco do Windows. Mexer na configuração de boot com ele ligado pode pedir a chave de recuperação.</summary>
    public static bool? BitLockerOn()
    {
        try
        {
            string drive = Environment.GetFolderPath(Environment.SpecialFolder.Windows)[..2];
            using var s = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{drive}'");
            foreach (ManagementObject o in s.Get()) using (o) return Convert.ToUInt32(o["ProtectionStatus"] ?? 0) == 1;
            return false;
        }
        catch { return null; }
    }

    /// <summary>Integridade de Memória (HVCI) rodando agora, pelo Win32_DeviceGuard.</summary>
    public static bool? HvciRunning()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            foreach (ManagementObject o in s.Get()) using (o) return o["SecurityServicesRunning"] is uint[] run ? run.Contains(2u) : o["SecurityServicesRunning"] is ushort[] r2 && r2.Contains((ushort)2);
            return null;
        }
        catch { return null; }
    }

    /// <summary>Versão do AMD Chipset Software (traz o driver de energia e os planos do Ryzen). Procura nos programas instalados.</summary>
    public static string AmdChipset()
    {
        foreach (var hive in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, hive).OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root == null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("DisplayName") is not string dn || !dn.Contains("AMD Chipset", StringComparison.OrdinalIgnoreCase)) continue;
                string ver = k.GetValue("DisplayVersion")?.ToString() ?? "?", date = k.GetValue("InstallDate")?.ToString() ?? "";
                string age = date.Length == 8 && DateTime.TryParseExact(date, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) && (DateTime.Now - d).TotalDays > 365
                    ? " (instalado há mais de um ano: veja a versão nova em amd.com/support)" : "";
                return $"AMD Chipset Software {ver}{age}.";
            }
        }
        return "AMD Chipset Software não encontrado: sem os planos de energia do Ryzen. Instale em amd.com/support (Chipsets › AMD B550).";
    }

    public static string Bcd() => Run("bcdedit.exe", "/enum {current}");

    /// <summary>Opção booleana do bcdedit ligada. O nome da opção não é traduzido; o valor pode vir como Yes/Sim/On.</summary>
    public static bool BcdFlag(string bcd, string name) => Regex.IsMatch(bcd, $@"(?im)^\s*{name}\s+(Yes|Sim|On|True)\s*$");
    public static bool BcdHas(string bcd, string name) => Regex.IsMatch(bcd, $@"(?im)^\s*{name}\s+\S");

    public static List<string> Diagnose()
    {
        var lines = new List<string>();
        string bcd = Bcd();
        if (bcd.Length == 0) lines.Add("bcdedit não respondeu (o app precisa estar como administrador).");
        else
        {
            lines.Add(BcdHas(bcd, "useplatformclock")
                ? $"HPET forçado (useplatformclock {(BcdFlag(bcd, "useplatformclock") ? "ligado" : "definido")}): aumenta a latência de medir o tempo. Remova com bcdedit /deletevalue useplatformclock."
                : "HPET: o Windows escolhe o timer (normal).");
            lines.Add(BcdFlag(bcd, "disabledynamictick") ? "Dynamic tick desligado (experimento ligado)." : "Dynamic tick: padrão do Windows.");
        }
        lines.Add(BitLockerOn() switch
        {
            true => "BitLocker LIGADO no disco do Windows: o experimento de dynamic tick fica bloqueado (mudar o boot pediria a chave de recuperação).",
            false => "BitLocker desligado.",
            _ => "BitLocker: não deu para ler.",
        });
        lines.Add(AmdChipset());
        lines.Add(ReadDword(PrioPath, "Win32PrioritySeparation") is int ps ? $"Win32PrioritySeparation = 0x{ps:X} (padrão do Windows: 0x2)." : "Win32PrioritySeparation: padrão.");
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, PNPClass FROM Win32_PnPEntity WHERE PNPClass = 'Display' OR PNPClass = 'Net'");
            foreach (ManagementObject o in s.Get())
                using (o)
                {
                    string id = o["PNPDeviceID"]?.ToString() ?? "";
                    if (!id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;
                    int? msi = ReadDword($@"SYSTEM\CurrentControlSet\Enum\{id}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties", "MSISupported");
                    lines.Add($"{o["Name"]}: interrupções {(msi == 1 ? "MSI (bom)" : "por linha (legado)")}" +
                              (msi == 1 ? "." : ". Trocar exige reiniciar e um dispositivo que não aceita pode não subir: só com o driver mais recente e um ponto de restauração."));
                }
        }
        catch (Exception ex) { lines.Add("Interrupções: não deu para ler (" + ex.Message + ")."); }
        return lines;
    }

    public static Tweak DynamicTick() => new()
    {
        Id = "exp.dynamictick", Slow = true, Title = "Experimento: desligar o dynamic tick (bcdedit)",
        Description = "O Windows para de pular os ticks do relógio quando ocioso. Mude, reinicie e meça como perfil da matriz; só mantenha se ganhar além do ruído.",
        Effect = "Em alguns PCs reduz travadas curtas; em outros não muda nada e gasta um pouco mais de energia. Bloqueado com BitLocker ligado.",
        NeedsRestart = true,
        Read = () => BcdFlag(Bcd(), "disabledynamictick"),
        Write = (on, s) =>
        {
            if (!on) { RestoreDynamicTick(s); return; }
            if (BitLockerOn() != false) throw new InvalidOperationException("BitLocker ligado (ou não deu para confirmar): mudar o boot pode pedir a chave de recuperação. Não alterei.");
            if (!s.TweakBackup.ContainsKey("exp.dynamictick")) { s.TweakBackup["exp.dynamictick"] = BcdHas(Bcd(), "disabledynamictick") ? "presente" : "<ausente>"; s.Save(); }
            Run("bcdedit.exe", "/set {current} disabledynamictick yes");
        },
        Restore = RestoreDynamicTick,
    };

    private static void RestoreDynamicTick(AppSettings s)
    {
        if (s.TweakBackup.GetValueOrDefault("exp.dynamictick") == "<ausente>") Run("bcdedit.exe", "/deletevalue {current} disabledynamictick");
    }

    public static Tweak PrioritySeparation() => new()
    {
        Id = "exp.prio", Title = "Experimento: prioridade do programa em primeiro plano (Win32PrioritySeparation 0x26)",
        Description = "Intervalos de CPU curtos e maiores para a janela em primeiro plano (o jogo). Meça como perfil da matriz.",
        Effect = "Pode estabilizar o frametime com programas em segundo plano; o efeito varia. Desmarcar volta ao valor original.",
        Read = () => ReadDword(PrioPath, "Win32PrioritySeparation") == 0x26,
        Write = (on, s) =>
        {
            if (on) WindowsTweaks.SetDword(s, "exp.prio", Registry.LocalMachine, PrioPath, "Win32PrioritySeparation", 0x26);
            else WindowsTweaks.RestoreDword(s, "exp.prio", Registry.LocalMachine, PrioPath, "Win32PrioritySeparation");
        },
        Restore = s => WindowsTweaks.RestoreDword(s, "exp.prio", Registry.LocalMachine, PrioPath, "Win32PrioritySeparation"),
    };

    private static int? ReadDword(string path, string name) => WindowsTweaks.ReadDword(Registry.LocalMachine, path, name);

    private static string Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return o;
        }
        catch { return ""; }
    }
}
