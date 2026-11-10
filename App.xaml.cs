using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace ResetExplode;

public partial class App : Application
{
    // Uma única instância: abrir o RESET EXPLODE de novo só traz a janela que já está aberta para a frente.
    private static Mutex? _single;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string title);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    protected override void OnStartup(StartupEventArgs e)
    {
        ErrorReporter.Init(this);
        bool first = false;
        // Depois de uma atualização, a versão antiga ainda está fechando: espera ela soltar a trava (até 20 s) em vez de desistir.
        int tries = e.Args.Contains(Updater.RestartArg) ? 40 : 1;
        for (int i = 0; i < tries && !first; i++)
        {
            if (i > 0) Thread.Sleep(500);
            try { _single?.Dispose(); _single = new Mutex(true, @"Local\ResetExplode.SingleInstance", out first); }
            catch (UnauthorizedAccessException) { first = false; } // criado por outra instância com outro nível de permissão
        }

        if (!first)
        {
            IntPtr h = FindWindow(null, "RESET EXPLODE");
            if (h != IntPtr.Zero)
            {
                if (IsIconic(h)) ShowWindow(h, 9); // SW_RESTORE
                SetForegroundWindow(h);
            }
            Shutdown();
            return;
        }
        base.OnStartup(e);
        new MainWindow().Show();
    }
}
