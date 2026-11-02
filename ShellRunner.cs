using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>
/// Abre programas, scripts, atalhos e links como o usuário comum, sem herdar o administrador do RESET EXPLODE.
/// Pede ao Explorer (o shell da área de trabalho, que roda sem elevação) que faça o ShellExecute.
/// É o método que a Microsoft indica para um processo elevado abrir algo sem elevação.
/// </summary>
public static class ShellRunner
{
    /// <summary>Abre <paramref name="file"/> (exe, bat, lnk, URL ou URI como steam://) sem privilégio de administrador.</summary>
    public static void Run(string file, string args = "", string? workingDir = null)
    {
        object shellApp;
        try { shellApp = DesktopShellApplication(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NullReferenceException)
        {
            // Sem o Explorer não há como abrir sem elevação; não abrimos como administrador no lugar.
            throw new InvalidOperationException("O Explorer do Windows não está rodando: não foi possível abrir sem privilégio de administrador.", ex);
        }
        try { ((dynamic)shellApp).ShellExecute(file, args, workingDir ?? "", "open", 1 /* SW_SHOWNORMAL */); }
        finally { Marshal.FinalReleaseComObject(shellApp); }
    }

    /// <summary>Abre um link no navegador padrão, sem elevação. Erros são ignorados (é só um atalho para a página).</summary>
    public static void OpenUrl(string url)
    {
        try { Run(url); } catch { }
    }

    // Shell.Application do Explorer: ShellWindows → janela da área de trabalho → IShellBrowser → IShellView → pasta de fundo → Application.
    private static object DesktopShellApplication()
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows, true)!)!;
        try
        {
            object loc = CSIDL_DESKTOP, root = null!;
            var desktop = shellWindows.FindWindowSW(ref loc, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
            var sp = (IServiceProvider)desktop;
            Guid sid = SID_STopLevelBrowser, iidBrowser = typeof(IShellBrowser).GUID, iidDispatch = IID_IDispatch;
            var browser = (IShellBrowser)sp.QueryService(ref sid, ref iidBrowser);
            browser.QueryActiveShellView(out var view);
            var background = view.GetItemObject(SVGIO_BACKGROUND, ref iidDispatch);
            return ((dynamic)background).Application;
        }
        finally { Marshal.FinalReleaseComObject(shellWindows); }
    }

    private const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;
    private const uint SVGIO_BACKGROUND = 0;
    private static readonly Guid CLSID_ShellWindows = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");

    // Os métodos _n só ocupam a posição na vtable (a ordem segue exdisp.idl / shobjidl.idl); nunca são chamados.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); // Count … OnActivated
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int phwnd, int swfwOptions);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid guidService, ref Guid riid);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        // IOleWindow: GetWindow, ContextSensitiveHelp; IShellBrowser: InsertMenusSB, SetMenuSB, RemoveMenusSB, SetStatusTextSB,
        // EnableModelessSB, TranslateAcceleratorSB, BrowseObject, GetViewStateStream, GetControlWindow, SendControlMsg (12 no total)
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12();
        void QueryActiveShellView(out IShellView ppshv);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        // IOleWindow: GetWindow, ContextSensitiveHelp; IShellView: TranslateAccelerator, EnableModeless, UIActivate, Refresh,
        // CreateViewWindow, DestroyViewWindow, GetCurrentInfo, AddPropertySheetPages, SaveViewState, SelectItem (12 no total)
        void _1(); void _2(); void _3(); void _4(); void _5(); void _6(); void _7(); void _8(); void _9(); void _10(); void _11(); void _12();
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetItemObject(uint uItem, ref Guid riid);
    }
}
