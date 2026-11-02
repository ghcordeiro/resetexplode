using System.Runtime.InteropServices;
using System.Text;

namespace ResetExplode;

/// <summary>
/// Limitador de FPS e modo de baixa latência no perfil do driver NVIDIA para um .exe, pela NVAPI DRS (o mesmo banco
/// que o Painel de Controle NVIDIA e o NVIDIA Profile Inspector editam). Vale para o jogo em qualquer lançamento, até
/// ser removido. Se o .exe não está em nenhum perfil, cria "RESET EXPLODE - jogo.exe"; ao desligar tudo, apaga esse perfil.
/// As estruturas são montadas à mão em memória (deslocamentos de nvapi.h, versão 1 de cada uma).
/// </summary>
public static class NvidiaProfiles
{
    public const uint FrameRateLimiter = 0x10835002; // FRL_FPS_ID: "Max Frame Rate" (0 = desligado)
    public const uint MaxPrerendered = 0x007BA09E;   // PRERENDERLIMIT_ID: 1 = "Modo de baixa latência: Ligado"
    public const string OurPrefix = "RESET EXPLODE - ";

    private const int UnicodeBytes = 2048 * 2;                       // NvAPI_UnicodeString
    private const int SettingSize = 12320, ProfileSize = 4116, AppSize = 12296;
    private const uint SettingVer = SettingSize | (1 << 16), ProfileVer = ProfileSize | (1 << 16), AppVer = AppSize | (1 << 16);

    public sealed record State(string Profile, bool Ours, uint? FpsLimit, uint? Prerendered);

    // ---- NVAPI: só nvapi_QueryInterface é exportada; cada função vem pelo ID ----
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnVoid();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnOutHandle(out IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnHandle(IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnFindApp(IntPtr s, IntPtr name, out IntPtr profile, IntPtr app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnFindProfile(IntPtr s, IntPtr name, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnCreateProfile(IntPtr s, IntPtr info, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnHandlePtr(IntPtr s, IntPtr profile, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnGetSetting(IntPtr s, IntPtr profile, uint id, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnDeleteSetting(IntPtr s, IntPtr profile, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnHandle2(IntPtr s, IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnNameFromId(uint id, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FnErrorMessage(int status, IntPtr text);

    private static T Fn<T>(uint id) where T : Delegate
    {
        IntPtr p = QueryInterface(id);
        if (p == IntPtr.Zero) throw new InvalidOperationException($"o driver não tem a função NVAPI 0x{id:X8}");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private static bool? _ok;

    /// <summary>Driver NVIDIA com NVAPI disponível (inicializa uma vez).</summary>
    public static bool Available
    {
        get
        {
            if (_ok is bool ok) return ok;
            try { _ok = Fn<FnVoid>(0x0150E828)() == 0; } // NvAPI_Initialize
            catch { _ok = false; }
            return _ok.Value;
        }
    }

    /// <summary>Nome que o driver dá à configuração (confirma que o ID é o esperado).</summary>
    public static string? SettingName(uint id)
    {
        if (!Available) return null;
        using var buf = new Buf(UnicodeBytes);
        return Fn<FnNameFromId>(0xD61CBE6E)(id, buf.Ptr) == 0 ? buf.ReadUnicode(0) : null;
    }

    /// <summary>Perfil e valores atuais para o .exe (só o nome do arquivo, ex.: cs2.exe), ou null se nenhum perfil tem esse .exe.</summary>
    public static State? Read(string exeName)
    {
        if (!Available) return null;
        return WithSession(s =>
        {
            if (FindApp(s, exeName) is not IntPtr profile) return null;
            string name = ProfileName(s, profile);
            return new State(name, name.StartsWith(OurPrefix), GetDword(s, profile, FrameRateLimiter), GetDword(s, profile, MaxPrerendered));
        }, save: false);
    }

    /// <summary>Grava (ou remove, com 0/false) o limite de FPS e a baixa latência do .exe e salva no driver. Devolve o resultado em texto.</summary>
    public static string Apply(string exeName, uint fpsLimit, bool lowLatency)
    {
        if (!Available) return "Driver NVIDIA (NVAPI) não encontrado.";
        return WithSession(s =>
        {
            IntPtr? found = FindApp(s, exeName);
            IntPtr profile;
            bool ours;
            if (found is IntPtr f) { profile = f; ours = ProfileName(s, f).StartsWith(OurPrefix); }
            else
            {
                if (fpsLimit == 0 && !lowLatency) return "Nada a remover: este jogo não tem perfil no driver.";
                profile = CreateProfileFor(s, exeName);
                ours = true;
            }

            if (fpsLimit > 0) Check(SetDword(s, profile, FrameRateLimiter, fpsLimit), "gravar o limite de FPS");
            else DeleteSetting(s, profile, FrameRateLimiter);
            if (lowLatency) Check(SetDword(s, profile, MaxPrerendered, 1), "gravar o modo de baixa latência");
            else DeleteSetting(s, profile, MaxPrerendered);

            if (ours && fpsLimit == 0 && !lowLatency)
            {
                Check(Fn<FnHandle2>(0x17093206)(s, profile), "apagar o perfil"); // NvAPI_DRS_DeleteProfile
                return "Perfil do RESET EXPLODE removido do driver.";
            }
            string where = ours ? "no perfil criado pelo RESET EXPLODE" : $"no perfil \"{ProfileName(s, profile)}\" do driver";
            return $"Gravado {where}: " + (fpsLimit > 0 ? $"limite de {fpsLimit} FPS" : "sem limite de FPS") +
                   (lowLatency ? ", baixa latência ligada." : ", baixa latência como o jogo pedir.");
        }, save: true) ?? "Falhou.";
    }

    // ------------------------------------------------------------------ sessão DRS

    private static T? WithSession<T>(Func<IntPtr, T?> work, bool save)
    {
        Check(Fn<FnOutHandle>(0x0694D52E)(out IntPtr s), "abrir a sessão do driver"); // NvAPI_DRS_CreateSession
        try
        {
            Check(Fn<FnHandle>(0x375DBD6B)(s), "ler os perfis do driver"); // NvAPI_DRS_LoadSettings
            var result = work(s);
            if (save) Check(Fn<FnHandle>(0xFCBC7E14)(s), "salvar os perfis do driver"); // NvAPI_DRS_SaveSettings
            return result;
        }
        finally { Fn<FnHandle>(0xDAD9CFF8)(s); } // NvAPI_DRS_DestroySession
    }

    private static IntPtr? FindApp(IntPtr s, string exe)
    {
        using var name = Buf.Unicode(exe);
        using var app = new Buf(AppSize);
        app.WriteU32(0, AppVer);
        // NvAPI_DRS_FindApplicationByName: diferente de 0 = não encontrado (ou erro, que a criação a seguir também acusa)
        return Fn<FnFindApp>(0xEEE566B2)(s, name.Ptr, out IntPtr profile, app.Ptr) == 0 ? profile : null;
    }

    private static IntPtr CreateProfileFor(IntPtr s, string exe)
    {
        string profileName = OurPrefix + exe;
        using var pname = Buf.Unicode(profileName);
        if (Fn<FnFindProfile>(0x7E4A9A0B)(s, pname.Ptr, out IntPtr existing) == 0) return existing; // NvAPI_DRS_FindProfileByName

        using var info = new Buf(ProfileSize);
        info.WriteU32(0, ProfileVer);
        info.WriteUnicode(4, profileName);
        info.WriteU32(4100, 1); // gpuSupport.geforce
        Check(Fn<FnCreateProfile>(0xCC176068)(s, info.Ptr, out IntPtr profile), "criar o perfil"); // NvAPI_DRS_CreateProfile

        using var app = new Buf(AppSize);
        app.WriteU32(0, AppVer);
        app.WriteUnicode(8, exe);          // appName
        app.WriteUnicode(4104, exe);       // userFriendlyName
        Check(Fn<FnHandlePtr>(0x4347A9DE)(s, profile, app.Ptr), "ligar o .exe ao perfil"); // NvAPI_DRS_CreateApplication
        return profile;
    }

    private static string ProfileName(IntPtr s, IntPtr profile)
    {
        using var info = new Buf(ProfileSize);
        info.WriteU32(0, ProfileVer);
        return Fn<FnHandlePtr>(0x61CD6FD6)(s, profile, info.Ptr) == 0 ? info.ReadUnicode(4) : ""; // NvAPI_DRS_GetProfileInfo
    }

    // NVDRS_SETTING_V1: version 0, settingName 4, settingId 4100, settingType 4104 (0 = DWORD), location 4108,
    // isCurrentPredefined 4112, isPredefinedValid 4116, valor predefinido 4120, valor atual 8220.
    private static int SetDword(IntPtr s, IntPtr profile, uint id, uint value)
    {
        using var st = new Buf(SettingSize);
        st.WriteU32(0, SettingVer);
        st.WriteU32(4100, id);
        st.WriteU32(4104, 0);
        st.WriteU32(8220, value);
        return Fn<FnHandlePtr>(0x577DD202)(s, profile, st.Ptr); // NvAPI_DRS_SetSetting
    }

    /// <summary>Valor gravado neste perfil (location 0 = o próprio perfil; herdado do global fica null).</summary>
    private static uint? GetDword(IntPtr s, IntPtr profile, uint id)
    {
        using var st = new Buf(SettingSize);
        st.WriteU32(0, SettingVer);
        if (Fn<FnGetSetting>(0x73BF8338)(s, profile, id, st.Ptr) != 0) return null; // NvAPI_DRS_GetSetting
        return st.ReadU32(4108) == 0 ? st.ReadU32(8220) : null;
    }

    private static void DeleteSetting(IntPtr s, IntPtr profile, uint id) =>
        Fn<FnDeleteSetting>(0xE4A26362)(s, profile, id); // NvAPI_DRS_DeleteProfileSetting (ausente = já está como padrão)

    private static void Check(int status, string what)
    {
        if (status == 0) return;
        string msg = $"código {status}";
        try
        {
            using var text = new Buf(64);
            if (Fn<FnErrorMessage>(0x6C2D048C)(status, text.Ptr) == 0) msg = Marshal.PtrToStringAnsi(text.Ptr) ?? msg; // NvAPI_GetErrorMessage
        }
        catch { }
        throw new InvalidOperationException($"NVIDIA: não foi possível {what} ({msg}).");
    }

    /// <summary>Bloco de memória zerado para as estruturas da NVAPI.</summary>
    private sealed class Buf : IDisposable
    {
        public readonly IntPtr Ptr;
        private readonly int _size;
        public Buf(int size) { _size = size; Ptr = Marshal.AllocHGlobal(size); Marshal.Copy(new byte[size], 0, Ptr, size); }
        public static Buf Unicode(string s) { var b = new Buf(UnicodeBytes); b.WriteUnicode(0, s); return b; }
        public void WriteU32(int off, uint v) => Marshal.WriteInt32(Ptr + off, unchecked((int)v));
        public uint ReadU32(int off) => unchecked((uint)Marshal.ReadInt32(Ptr + off));
        public void WriteUnicode(int off, string s)
        {
            var bytes = Encoding.Unicode.GetBytes(s.Length > 2047 ? s[..2047] : s);
            Marshal.Copy(bytes, 0, Ptr + off, Math.Min(bytes.Length, _size - off - 2));
        }
        public string ReadUnicode(int off) => Marshal.PtrToStringUni(Ptr + off) ?? "";
        public void Dispose() => Marshal.FreeHGlobal(Ptr);
    }
}
