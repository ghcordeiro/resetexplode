using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ResetExplode;

/// <summary>
/// Lê o mouse (e, só quando pedido, o teclado) pelo Raw Input numa thread própria com janela invisível (message-only),
/// registrada com RIDEV_INPUTSINK para receber mesmo com o jogo em primeiro plano. Guarda SÓ o instante de chegada de
/// cada relatório: nada de posição, botões ou tecla. Um registro por processo (regra do Windows), dividido entre
/// assinantes (<see cref="Tap"/>): a sessão de jogo e os testes avulsos.
/// </summary>
public sealed class RawInputMonitor
{
    public static RawInputMonitor Shared { get; } = new();
    private RawInputMonitor() { }

    public sealed class Tap : IDisposable
    {
        private readonly RawInputMonitor _owner;
        internal readonly List<double> Mouse = new(), Keyboard = new();
        internal readonly bool WantsKeyboard;
        internal Tap(RawInputMonitor owner, bool keyboard) { _owner = owner; WantsKeyboard = keyboard; }

        /// <summary>Instantes (ms, relógio de alta precisão) dos relatórios de movimento do mouse desde a última leitura.</summary>
        public double[] DrainMouse() { lock (_owner._lock) { var a = Mouse.ToArray(); Mouse.Clear(); return a; } }
        public double[] DrainKeyboard() { lock (_owner._lock) { var a = Keyboard.ToArray(); Keyboard.Clear(); return a; } }
        public void Dispose() => _owner.Unsubscribe(this);
    }

    private readonly object _lock = new();
    private readonly List<Tap> _taps = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    private bool _keyboardOn;
    // Classe de janela registrada uma vez por processo; o delegate fica vivo para sempre (o Windows guarda o ponteiro dele).
    private static WndProc? _proc;
    private static string? _className;
    private readonly ManualResetEventSlim _ready = new();
    public string? Error { get; private set; }

    public Tap Subscribe(bool keyboard)
    {
        var tap = new Tap(this, keyboard);
        lock (_lock) _taps.Add(tap);
        EnsureRunning();
        UpdateRegistration();
        return tap;
    }

    private void Unsubscribe(Tap tap)
    {
        bool stop;
        lock (_lock) { _taps.Remove(tap); stop = _taps.Count == 0; }
        if (stop) Stop(); else UpdateRegistration();
    }

    private void EnsureRunning()
    {
        lock (_lock)
        {
            if (_thread is { IsAlive: true }) return;
            _ready.Reset();
            Error = null;
            _thread = new Thread(Run) { IsBackground = true, Name = "Raw Input (mouse/teclado)", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }
        _ready.Wait(3000);
    }

    private void Stop()
    {
        IntPtr h;
        lock (_lock) { h = _hwnd; }
        if (h != IntPtr.Zero) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
    }

    /// <summary>Teclado registrado só enquanto algum assinante pede (o teste avulso ou a opção da sessão).</summary>
    private void UpdateRegistration()
    {
        IntPtr h; bool want;
        lock (_lock) { h = _hwnd; want = _taps.Any(t => t.WantsKeyboard); }
        if (h != IntPtr.Zero && want != _keyboardOn) PostMessage(h, WM_APP_KEYBOARD, new IntPtr(want ? 1 : 0), IntPtr.Zero);
    }

    private void Run()
    {
        try
        {
            IntPtr inst = GetModuleHandle(null);
            if (_className == null)
            {
                _proc = Shared.Proc;
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = _proc, hInstance = inst,
                    lpszClassName = "ResetExplodeRawInput" + Environment.ProcessId,
                };
                if (RegisterClassEx(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx falhou ({Marshal.GetLastWin32Error()})");
                _className = wc.lpszClassName;
            }
            var hwnd = CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0, new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, inst, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx falhou ({Marshal.GetLastWin32Error()})");
            if (!Register(hwnd, mouse: true, keyboard: null)) throw new InvalidOperationException($"RegisterRawInputDevices falhou ({Marshal.GetLastWin32Error()})");
            lock (_lock) _hwnd = hwnd;
            _ready.Set();
            UpdateRegistration();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            lock (_lock) { _hwnd = IntPtr.Zero; _keyboardOn = false; }
            _ready.Set();
        }
    }

    private bool Register(IntPtr hwnd, bool mouse, bool? keyboard)
    {
        var list = new List<RAWINPUTDEVICE>();
        if (mouse) list.Add(new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd });
        if (keyboard == true) list.Add(new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd });
        if (keyboard == false) list.Add(new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero });
        return list.Count == 0 || RegisterRawInputDevices(list.ToArray(), (uint)list.Count, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private unsafe IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
            {
                double now = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                byte* buf = stackalloc byte[64];
                uint size = 64;
                if (GetRawInputData(lParam, RID_INPUT, (IntPtr)buf, ref size, (uint)sizeof(RAWINPUTHEADER)) != uint.MaxValue)
                {
                    var header = (RAWINPUTHEADER*)buf;
                    lock (_lock)
                    {
                        if (header->dwType == RIM_TYPEMOUSE)
                        {
                            // RAWMOUSE logo após o cabeçalho: lLastX em +12, lLastY em +16. Só conta relatório com movimento.
                            byte* m = buf + sizeof(RAWINPUTHEADER);
                            if (*(int*)(m + 12) != 0 || *(int*)(m + 16) != 0)
                                foreach (var t in _taps) t.Mouse.Add(now);
                        }
                        else if (header->dwType == RIM_TYPEKEYBOARD)
                            foreach (var t in _taps) if (t.WantsKeyboard) t.Keyboard.Add(now);
                    }
                }
                break; // WM_INPUT precisa de DefWindowProc para o Windows liberar o buffer
            }
            case WM_APP_KEYBOARD:
                if (Register(hwnd, mouse: false, keyboard: wParam != IntPtr.Zero)) _keyboardOn = wParam != IntPtr.Zero;
                return IntPtr.Zero;
            case WM_DESTROY:
                Register(hwnd, mouse: false, keyboard: false);
                RegisterRawInputDevices(new[] { new RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = RIDEV_REMOVE } }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private const uint WM_INPUT = 0x00FF, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_APP_KEYBOARD = 0x8000 + 1;
    private const uint RID_INPUT = 0x10000003, RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001;
    private const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }
    [StructLayout(LayoutKind.Sequential)] private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}

/// <summary>Mouse (e teclado, se o usuário ligou a opção) durante a sessão: taxa de relatórios e irregularidade por segundo.</summary>
public sealed class InputProbe : SessionProbe
{
    private readonly bool _keyboard;
    private RawInputMonitor.Tap? _tap;
    private double _lastMouse = double.NaN;
    private readonly List<double> _hz = new();

    public InputProbe(bool keyboard) => _keyboard = keyboard;

    public override IReadOnlyList<string> Columns { get; } = new[] { "mouse_hz", "mouse_jitter_ms", "mouse_relatorios", "teclado_eventos" };

    public override void Start(int pid, string exe, string folder) => _tap = RawInputMonitor.Shared.Subscribe(_keyboard);

    public override void Fill(double?[] v, int o)
    {
        if (_tap == null) return;
        var m = _tap.DrainMouse();
        // Junta com o último relatório do segundo anterior para não perder o intervalo da virada.
        var times = double.IsNaN(_lastMouse) ? m : new[] { _lastMouse }.Concat(m).ToArray();
        if (m.Length > 0) _lastMouse = m[^1];
        var (hz, jitter) = InputAnalysis.MouseRate(times);
        v[o] = hz; v[o + 1] = jitter; v[o + 2] = m.Length;
        if (hz is double h) _hz.Add(h);
        if (_keyboard) v[o + 3] = _tap.DrainKeyboard().Length;
    }

    public override Dictionary<string, object?> Finish()
    {
        _tap?.Dispose(); _tap = null;
        if (_hz.Count == 0) return new() { ["mouse"] = null };
        _hz.Sort();
        double med = _hz[_hz.Count / 2];
        return new() { ["mouse"] = new { hz_mediano = Math.Round(med), padrao = InputAnalysis.NearestStandard(med), segundos_com_movimento = _hz.Count } };
    }

    public override void Dispose() { _tap?.Dispose(); _tap = null; }
}
