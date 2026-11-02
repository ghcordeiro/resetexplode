using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ResetExplode;

/// <summary>
/// Tela de teste de atraso: janela sem borda cobrindo o monitor principal, preta, que fica branca no instante do clique.
/// Renderiza sem parar com Present(0) e tearing (flip discard), como um jogo competitivo sem V-Sync, para a mudança
/// aparecer na próxima linha que o monitor desenhar. Thread própria (janela + Direct3D 11). ESC fecha.
/// </summary>
public sealed class LatencyWindow : ILatencyScreen, IDisposable
{
    private Thread? _thread;
    private volatile bool _stop, _white;
    private readonly ConcurrentQueue<Action> _flashes = new();
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hwnd;
    private static WndProc? _proc;      // vivo para sempre: o Windows guarda o ponteiro
    private static LatencyWindow? _current;
    private const string ClassName = "ResetExplodeLatencyTest";

    /// <summary>Clique deixa a tela branca (true no teste com Arduino e no modo câmera).</summary>
    public bool WhiteOnClick { get; init; } = true;
    /// <summary>Soltar o botão volta ao preto (modo câmera, quando é a pessoa que clica).</summary>
    public bool BlackOnRelease { get; init; }
    public string? Error { get; private set; }
    /// <summary>Posição e tamanho da janela; padrão = monitor principal inteiro.</summary>
    public (int X, int Y, int W, int H)? Bounds { get; init; }
    /// <summary>Frames apresentados (a tela de teste roda sem limite de FPS).</summary>
    public long Frames => Interlocked.Read(ref _frames);
    public IntPtr Handle => _hwnd;
    private long _frames;
    /// <summary>ESC ou a janela foi fechada.</summary>
    public event Action? Closed;

    public bool White { get => _white; set => _white = value; }

    public void FlashWith(Action beforePresent) => _flashes.Enqueue(beforePresent);

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Tela de teste de atraso", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(5000);
        if (Error != null) throw new InvalidOperationException(Error);
    }

    private void Run()
    {
        var keep = new List<IDisposable>();
        T K<T>(T o) where T : IDisposable { keep.Add(o); return o; }
        try
        {
            IntPtr inst = GetModuleHandleW(null);
            if (_proc == null)
            {
                _proc = StaticProc;
                var wc = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = inst, lpszClassName = ClassName, hbrBackground = GetStockObject(4 /* BLACK_BRUSH */),
                };
                RegisterClassExW(ref wc);
            }
            _current = this;
            var (x, y, w, h) = Bounds ?? (0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
            _hwnd = CreateWindowExW(0x00000008 /* WS_EX_TOPMOST */, ClassName, "RESET EXPLODE · teste de atraso", 0x80000000 | 0x10000000 /* WS_POPUP | WS_VISIBLE */,
                                    x, y, w, h, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Não foi possível criar a tela de teste.");
            SetForegroundWindow(_hwnd);
            SetCursorPos(x + w / 2, y + h / 2); // o clique do Arduino cai no meio da tela de teste

            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                                    new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device device, out ID3D11DeviceContext ctx).CheckError();
            K(device); K(ctx);
            var factory = K(DXGI.CreateDXGIFactory1<IDXGIFactory2>());
            bool tearing;
            using (var f5 = factory.QueryInterfaceOrNull<IDXGIFactory5>()) tearing = f5?.PresentAllowTearing ?? false;
            var swap = K(factory.CreateSwapChainForHwnd(device, _hwnd,
                new SwapChainDescription1((uint)w, (uint)h, Format.B8G8R8A8_UNorm, false, Usage.RenderTargetOutput, 2,
                                          Scaling.Stretch, SwapEffect.FlipDiscard, AlphaMode.Ignore, tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None)));
            factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);
            // No modelo flip do D3D11 o buffer 0 é sempre o back buffer atual: uma única RTV serve.
            using var back = swap.GetBuffer<ID3D11Texture2D>(0);
            var rtv = K(device.CreateRenderTargetView(back));
            var flags = tearing ? PresentFlags.AllowTearing : PresentFlags.None;
            _ready.Set();

            while (!_stop)
            {
                while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
                if (_flashes.TryDequeue(out var before))
                {
                    _white = true;
                    ctx.ClearRenderTargetView(rtv, new Color4(1, 1, 1, 1));
                    before(); // o PC avisa o Arduino junto com este frame
                    swap.Present(0, flags);
                    Interlocked.Increment(ref _frames);
                    continue;
                }
                float c = _white ? 1 : 0;
                ctx.ClearRenderTargetView(rtv, new Color4(c, c, c, 1));
                swap.Present(0, flags);
                Interlocked.Increment(ref _frames);
            }
        }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            for (int i = keep.Count - 1; i >= 0; i--) try { keep[i].Dispose(); } catch { }
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
            if (_current == this) _current = null;
            _ready.Set();
            Closed?.Invoke();
        }
    }

    private static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;
        if (self != null)
            switch (msg)
            {
                case 0x0201: if (self.WhiteOnClick) self._white = true; return IntPtr.Zero;        // WM_LBUTTONDOWN
                case 0x0202: if (self.BlackOnRelease) self._white = false; return IntPtr.Zero;     // WM_LBUTTONUP
                case 0x0100 when wParam == (IntPtr)0x1B: self._stop = true; return IntPtr.Zero;    // WM_KEYDOWN ESC
                case 0x0020: SetCursor(IntPtr.Zero); return (IntPtr)1;                            // WM_SETCURSOR: sem cursor
                case 0x0010: self._stop = true; return IntPtr.Zero;                                // WM_CLOSE
            }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        _stop = true;
        _thread?.Join(3000);
    }

    // ------------------------------------------------------------------ Win32

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
}
