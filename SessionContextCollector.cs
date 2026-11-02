using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ResetExplode;

/// <summary>
/// Monta o <see cref="SessionContext"/> no Windows: RAM (WMI), GPU (NVML), monitor do jogo (EnumDisplaySettings + WMI),
/// opções do Windows e o disco onde o jogo está. Cada parte falha sozinha (o erro vai para ErrosColeta).
/// Roda fora da thread da tela: algumas consultas WMI levam centenas de ms.
/// </summary>
public static class SessionContextCollector
{
    public static SessionContext Collect(string exePath, IntPtr gameWindow, IntPtr nvml, string? cpu, string? gpu, string? driver,
                                         IEnumerable<string> tweaksOn)
    {
        var c = new SessionContext { Cpu = cpu, Gpu = gpu, DriverGpu = driver };
        Try(c, "windows", () => c.Windows = WindowsVersion());
        Try(c, "ram", () => c.Ram = RamModules());
        if (nvml != IntPtr.Zero)
            Try(c, "gpu", () =>
            {
                if (Nvml.Bar1(nvml) is Nvml.Memory bar && bar.Total > 0) c.ResizableBar = bar.Total > 256UL * 1024 * 1024;
                c.PcieMaxGeracao = Nvml.MaxPcieGen(nvml);
                c.PcieMaxLargura = Nvml.MaxPcieWidth(nvml);
            });
        Try(c, "monitor", () => c.Monitor = MonitorOf(gameWindow));
        Try(c, "vrr", () => c.VrrWindows = DxUserSetting("VRROptimizeEnable") is string v ? v == "1" : null);
        Try(c, "energia", () => c.PlanoEnergia = WindowsTweaks.ActiveSchemeName());
        Try(c, "hags", () => c.Hags = RegDword(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode") is int h ? h == 2 : null);
        Try(c, "modo_jogo", () => c.ModoJogo = (RegDword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled") ?? 1) == 1);
        Try(c, "ajustes", () => c.AjustesLigados = tweaksOn.ToList());
        if (exePath.Length > 2) Try(c, "disco", () => c.Disco = DiskOf(exePath));
        return c;
    }

    /// <summary>Link PCIe atual (para guardar o maior visto durante o jogo).</summary>
    public static (uint? Gen, uint? Width) PcieNow(IntPtr nvml) =>
        nvml == IntPtr.Zero ? (null, null) : (Nvml.CurPcieGen(nvml), Nvml.CurPcieWidth(nvml));

    private static void Try(SessionContext c, string part, Action a)
    {
        try { a(); }
        catch (Exception ex) { c.ErrosColeta.Add($"{part}: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ Windows e registro

    private static string WindowsVersion()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string name = k?.GetValue("ProductName") as string ?? "Windows";
        if (k?.GetValue("CurrentBuild") is string b && int.TryParse(b, out int build) && build >= 22000) name = name.Replace("Windows 10", "Windows 11");
        return $"{name} {k?.GetValue("DisplayVersion")} (build {k?.GetValue("CurrentBuild")}.{k?.GetValue("UBR")})";
    }

    private static int? RegDword(RegistryKey hive, string path, string name)
    {
        using var k = hive.OpenSubKey(path);
        return k?.GetValue(name) is int v ? v : null;
    }

    private static string? DxUserSetting(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        foreach (var part in (k?.GetValue("DirectXUserGlobalSettings") as string ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim() == key) return kv[1].Trim();
        }
        return null;
    }

    // ------------------------------------------------------------------ RAM

    private static List<SessionContext.ModuloRam> RamModules()
    {
        var list = new List<SessionContext.ModuloRam>();
        using var s = new ManagementObjectSearcher(
            "SELECT BankLabel, DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, PartNumber, FormFactor FROM Win32_PhysicalMemory");
        foreach (ManagementObject m in s.Get())
            using (m)
            {
                list.Add(new SessionContext.ModuloRam
                {
                    Slot = $"{m["BankLabel"]} {m["DeviceLocator"]}".Trim(),
                    CapacidadeGb = Convert.ToDouble(m["Capacity"] ?? 0) / (1L << 30),
                    Tipo = Convert.ToInt32(m["SMBIOSMemoryType"] ?? 0) switch
                    {
                        24 => "DDR3", 26 => "DDR4", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", var t => $"tipo {t}"
                    },
                    VelocidadeNominalMts = Convert.ToInt32(m["Speed"] ?? 0),
                    VelocidadeConfiguradaMts = Convert.ToInt32(m["ConfiguredClockSpeed"] ?? 0),
                    PartNumber = (m["PartNumber"]?.ToString() ?? "").Trim(),
                    Sodimm = Convert.ToInt32(m["FormFactor"] ?? 0) == 12,
                });
            }
        return list;
    }

    // ------------------------------------------------------------------ disco do jogo

    private static SessionContext.DiscoJogo? DiskOf(string exePath)
    {
        string root = Path.GetPathRoot(exePath) ?? "";
        if (root.Length < 2 || root[1] != ':') return null;
        char letter = char.ToUpperInvariant(root[0]);
        var d = new SessionContext.DiscoJogo { Unidade = $"{letter}:" };
        const string ns = @"root\Microsoft\Windows\Storage";
        uint? disk = null;
        using (var q = new ManagementObjectSearcher(ns, $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter = '{letter}'"))
            foreach (ManagementObject p in q.Get()) using (p) disk = Convert.ToUInt32(p["DiskNumber"]);
        if (disk == null) return d;
        using (var q = new ManagementObjectSearcher(ns, $"SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk WHERE DeviceId = '{disk}'"))
            foreach (ManagementObject p in q.Get())
                using (p)
                {
                    d.Modelo = p["FriendlyName"]?.ToString()?.Trim() ?? "";
                    int media = Convert.ToInt32(p["MediaType"] ?? 0), bus = Convert.ToInt32(p["BusType"] ?? 0);
                    // BusType 7 = USB, 17 = NVMe; MediaType 3 = HDD, 4 = SSD
                    d.Tipo = bus == 7 ? "Externo (USB)" : bus == 17 ? "SSD NVMe" : media == 4 ? "SSD" : media == 3 ? "HD" : "desconhecido";
                }
        return d;
    }

    // ------------------------------------------------------------------ monitor do jogo

    private static SessionContext.MonitorJogo? MonitorOf(IntPtr hwnd)
    {
        IntPtr mon = hwnd != IntPtr.Zero ? MonitorFromWindow(hwnd, 2) : MonitorFromPoint(default, 1);
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return null;

        var cur = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(mi.szDevice, -1 /* ENUM_CURRENT_SETTINGS */, ref cur)) return null;
        var m = new SessionContext.MonitorJogo { Largura = cur.dmPelsWidth, Altura = cur.dmPelsHeight, HzAtual = cur.dmDisplayFrequency };

        var mode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettings(mi.szDevice, i, ref mode); i++)
        {
            if (mode.dmPelsWidth == cur.dmPelsWidth && mode.dmPelsHeight == cur.dmPelsHeight)
                m.HzMaximo = Math.Max(m.HzMaximo, mode.dmDisplayFrequency);
            if ((long)mode.dmPelsWidth * mode.dmPelsHeight > (long)m.LarguraNativa * m.AlturaNativa)
                (m.LarguraNativa, m.AlturaNativa) = (mode.dmPelsWidth, mode.dmPelsHeight);
        }

        // Nome e tipo de conexão pelo WMI, casando o ID do monitor (DISPLAY\GSM5B7F\5&...&UID4353).
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (EnumDisplayDevices(mi.szDevice, 0, ref dd, 1 /* EDD_GET_DEVICE_INTERFACE_NAME */))
        {
            m.Nome = dd.DeviceString;
            string id = dd.DeviceID; // \\?\DISPLAY#GSM5B7F#5&2c1e8a6c&0&UID4353#{e6f07b5f-...}
            var parts = id.Split('#');
            if (parts.Length >= 3)
            {
                string instance = $@"{parts[0].Replace(@"\\?\", "")}\{parts[1]}\{parts[2]}";
                try
                {
                    using var q = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID");
                    foreach (ManagementObject o in q.Get())
                        using (o)
                            if ((o["InstanceName"] as string)?.StartsWith(instance, StringComparison.OrdinalIgnoreCase) == true &&
                                o["UserFriendlyName"] is ushort[] chars)
                            {
                                string name = new string(chars.TakeWhile(ch => ch != 0).Select(ch => (char)ch).ToArray()).Trim();
                                if (name.Length > 0) m.Nome = name;
                            }
                    using var q2 = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
                    foreach (ManagementObject o in q2.Get())
                        using (o)
                            if ((o["InstanceName"] as string)?.StartsWith(instance, StringComparison.OrdinalIgnoreCase) == true)
                                m.Conexao = Convert.ToUInt32(o["VideoOutputTechnology"]) switch
                                {
                                    0 => "VGA", 4 => "DVI", 5 => "HDMI", 10 or 11 => "DisplayPort", 0x80000000 => "Interna", 15 => "Miracast", var t => $"tipo {t}"
                                };
                }
                catch { /* WMI do monitor indisponível: fica só com o que a API de vídeo deu */ }
            }
        }
        return m;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
}
