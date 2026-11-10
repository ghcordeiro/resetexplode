using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text.Json;

namespace ResetExplode;

public sealed class UnusedDevice
{
    public string InstanceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Why { get; init; } = "";
    public bool Selected { get; set; }
    public string Title => $"{Name}  ·  {Kind}";
}

/// <summary>
/// Desabilita dispositivos que não servem a este PC (adaptador de rede sem cabo, Bluetooth, controladoras de áudio sem uso). Só classes de
/// rede, Bluetooth e som; GPU, disco, USB, teclado e mouse nunca aparecem. Reversível: o Instance ID vai para um arquivo antes de desabilitar.
/// </summary>
public static class DeviceCleanup
{
    private static string RemovedPath => Path.Combine(AppSettings.Folder, "dispositivos-desabilitados.json");

    public static List<UnusedDevice> Scan()
    {
        var list = new List<UnusedDevice>();
        var offline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PNPDeviceID, NetConnectionStatus, PhysicalAdapter FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE");
            foreach (ManagementObject o in s.Get())
                using (o)
                    if (o["PNPDeviceID"] is string id && Convert.ToInt32(o["NetConnectionStatus"] ?? 0) != 2) offline.Add(id);
        }
        catch { }

        using var q = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, PNPClass, Status, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Net' OR PNPClass = 'Bluetooth' OR PNPClass = 'MEDIA'");
        foreach (ManagementObject o in q.Get())
            using (o)
            {
                string id = o["PNPDeviceID"]?.ToString() ?? "", name = o["Name"]?.ToString() ?? "", cls = o["PNPClass"]?.ToString() ?? "";
                if (id.Length == 0 || Convert.ToInt32(o["ConfigManagerErrorCode"] ?? 0) == 22) continue; // 22 = já desabilitado
                if (cls.Equals("Net", StringComparison.OrdinalIgnoreCase))
                {
                    if (!offline.Contains(id)) continue; // adaptador com cabo/Wi-Fi conectado fica
                    list.Add(new UnusedDevice { InstanceId = id, Name = name, Kind = "rede sem conexão", Why = "Adaptador físico sem conexão: gera interrupções e consulta à toa." });
                }
                else if (cls.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase))
                    list.Add(new UnusedDevice { InstanceId = id, Name = name, Kind = "Bluetooth", Why = "Você disse que não usa Bluetooth." });
                else if (cls.Equals("MEDIA", StringComparison.OrdinalIgnoreCase) && name.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase) && id.StartsWith(@"HDAUDIO\", StringComparison.OrdinalIgnoreCase))
                    list.Add(new UnusedDevice { InstanceId = id, Name = name, Kind = "áudio (controladora)", Why = "Só desabilite se você NÃO usa essa saída de som (HDMI/DisplayPort da GPU ou o áudio da placa-mãe). Confira antes: sem ela, essa saída fica muda." });
            }
        return list.OrderBy(d => d.Kind).ThenBy(d => d.Name).ToList();
    }

    public static (List<string> Done, List<string> Failed) Disable(IEnumerable<UnusedDevice> devices)
    {
        var done = new List<string>(); var failed = new List<string>();
        var saved = Load();
        foreach (var d in devices)
        {
            saved[d.InstanceId] = d.Name; // grava antes
            File.WriteAllText(RemovedPath, JsonSerializer.Serialize(saved));
            var (code, text) = Pnp($"/disable-device \"{d.InstanceId}\"");
            if (code == 0) done.Add(d.Name); else { failed.Add($"{d.Name} ({text.Trim().Split('\n')[0]})"); saved.Remove(d.InstanceId); File.WriteAllText(RemovedPath, JsonSerializer.Serialize(saved)); }
        }
        return (done, failed);
    }

    public static (int Restored, List<string> Failed) EnableAll()
    {
        var saved = Load();
        int ok = 0; var failed = new List<string>();
        foreach (var (id, name) in saved.ToList())
        {
            var (code, text) = Pnp($"/enable-device \"{id}\"");
            if (code == 0) { ok++; saved.Remove(id); } else failed.Add($"{name} ({text.Trim().Split('\n')[0]})");
        }
        File.WriteAllText(RemovedPath, JsonSerializer.Serialize(saved));
        return (ok, failed);
    }

    public static int DisabledCount => Load().Count;

    private static Dictionary<string, string> Load()
    {
        try { if (File.Exists(RemovedPath)) return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RemovedPath)) ?? new(); }
        catch { }
        return new();
    }

    private static (int, string) Pnp(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("pnputil.exe", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            return (p.ExitCode, o);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
