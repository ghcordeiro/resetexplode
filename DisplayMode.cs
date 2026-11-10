using System.IO;
using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>
/// Troca temporária do modo de vídeo do monitor principal (ex.: área de trabalho em 1280x960 enquanto o CS2 roda em janela sem borda,
/// para o alt-tab não trocar o modo do monitor). O modo original vai para um arquivo antes da troca: se o app fechar no meio,
/// <see cref="RestorePending"/> devolve na próxima abertura.
/// </summary>
public static class DisplayMode
{
    public sealed record Mode(int Width, int Height, int Hz);

    private static string PendingPath => Path.Combine(AppSettings.Folder, "display-restore.txt");

    public static Mode? Current()
    {
        var dm = New();
        return EnumDisplaySettings(null, -1, ref dm) ? new Mode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency) : null;
    }

    /// <summary>Troca o monitor principal para <paramref name="width"/>x<paramref name="height"/> na maior taxa que ele aceita nessa resolução (no máximo a atual). Devolve o modo anterior, ou o erro.</summary>
    public static (Mode? Previous, string? Error) SwitchPrimary(int width, int height)
    {
        var old = Current();
        if (old == null) return (null, "não consegui ler o modo atual do monitor");
        if (old.Width == width && old.Height == height) return (null, null); // já está nessa resolução: nada a trocar nem a restaurar

        int hz = 0;
        var m = New();
        for (int i = 0; EnumDisplaySettings(null, i, ref m); i++)
            if (m.dmPelsWidth == width && m.dmPelsHeight == height && m.dmDisplayFrequency <= Math.Max(old.Hz, 60)) hz = Math.Max(hz, m.dmDisplayFrequency);
        if (hz == 0) return (null, $"o monitor não oferece {width}x{height}");

        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(PendingPath, $"{old.Width}x{old.Height}@{old.Hz}");
        }
        catch (Exception ex) { return (null, "não consegui guardar o modo original: " + ex.Message); }

        int rc = Apply(width, height, hz);
        if (rc != 0) { try { File.Delete(PendingPath); } catch { } return (null, $"o Windows recusou o modo (código {rc})"); }
        return (old, null);
    }

    public static void Restore(Mode mode)
    {
        Apply(mode.Width, mode.Height, mode.Hz);
        try { File.Delete(PendingPath); } catch { }
    }

    /// <summary>Se o app fechou com o modo trocado, devolve o original. Chamado na abertura.</summary>
    public static void RestorePending()
    {
        try
        {
            if (!File.Exists(PendingPath)) return;
            var parts = File.ReadAllText(PendingPath).Trim().Split('x', '@');
            if (parts.Length == 3 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h) && int.TryParse(parts[2], out int hz))
                Restore(new Mode(w, h, hz));
            else File.Delete(PendingPath);
        }
        catch { }
    }

    private static int Apply(int w, int h, int hz)
    {
        var dm = New();
        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmDisplayFrequency = hz;
        dm.dmFields = 0x80000 | 0x100000 | 0x400000; // largura, altura, frequência
        return ChangeDisplaySettingsEx(null, ref dm, IntPtr.Zero, 0 /* dinâmico: não grava no registro */, IntPtr.Zero);
    }

    private static DEVMODE New() => new() { dmSize = (short)Marshal.SizeOf<DEVMODE>() };

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string? deviceName, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr lParam);
}
