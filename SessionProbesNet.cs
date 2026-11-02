using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace ResetExplode;

/// <summary>
/// Rede durante o jogo: ping a cada 1 s para o roteador e para a internet (1.1.1.1), tráfego do processo do jogo e
/// o endereço com quem ele mais troca dados (servidor ou relay), lidos via ETW (Microsoft-Windows-Kernel-Network).
/// Se esse servidor responde a ping, ele também é medido. Separa "lag de rede" de "engasgo do PC".
/// </summary>
public sealed class NetworkProbe : SessionProbe
{
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    private static readonly IPAddress Internet = IPAddress.Parse("1.1.1.1");
    private const string SessionName = "ResetExplode-Rede";

    private int _pid;
    private IPAddress? _gateway;
    private TraceEventSession? _etw;
    private Thread? _etwThread, _pingThread;
    private volatile bool _stop;
    private long _rx, _tx, _rxTotal, _txTotal;
    private readonly object _lock = new();
    private readonly Dictionary<string, long> _bytesByRemote = new();
    private string? _server;
    private int _serverFails;
    private bool _serverAnswered;
    private double? _pingGw, _pingNet, _pingServer;
    private bool _lostNet;
    private readonly List<double> _gwAll = new(), _netAll = new(), _srvAll = new();
    private int _gwSent, _netSent, _gwLost, _netLost;
    private long _lastFill = System.Diagnostics.Stopwatch.GetTimestamp();

    public override IReadOnlyList<string> Columns { get; } = new[]
    {
        "ping_roteador_ms", "ping_internet_ms", "ping_internet_perdido", "ping_servidor_ms", "jogo_rede_rx_kbps", "jogo_rede_tx_kbps"
    };

    public string? Server { get { lock (_lock) return _server; } }

    public override void Start(int pid, string exe, string folder)
    {
        _pid = pid;
        _gateway = NetworkTest.DefaultGateway();
        _pingThread = new Thread(PingLoop) { IsBackground = true, Name = "Sessão: ping" };
        _pingThread.Start();
        try
        {
            _etw = new TraceEventSession(SessionName, TraceEventSessionOptions.Create) { StopOnDispose = true };
            _etw.EnableProvider(KernelNetwork, TraceEventLevel.Informational, ulong.MaxValue);
            var parser = new RegisteredTraceEventParser(_etw.Source);
            parser.All += OnNetEvent;
            _etwThread = new Thread(() => { try { _etw.Source.Process(); } catch { } }) { IsBackground = true, Name = "Sessão: ETW rede" };
            _etwThread.Start();
        }
        catch { _etw?.Dispose(); _etw = null; } // sem ETW: fica só com os pings
    }

    // IDs do Kernel-Network: TCP enviado/recebido (IPv4 10/11, IPv6 26/27), UDP enviado/recebido (IPv4 42/43, IPv6 58/59).
    private void OnNetEvent(TraceEvent e)
    {
        int id = (int)e.ID;
        bool rx = id is 11 or 27 or 43 or 59, tx = id is 10 or 26 or 42 or 58;
        if (!rx && !tx) return;
        if (ToLong(e.PayloadByName("PID")) != _pid) return;
        long size = ToLong(e.PayloadByName("size"));
        if (size <= 0) return;
        if (rx) Interlocked.Add(ref _rx, size); else Interlocked.Add(ref _tx, size);
        string? remote = Address(e.PayloadByName(rx ? "saddr" : "daddr"));
        if (remote != null && IsPublic(remote))
            lock (_lock) _bytesByRemote[remote] = _bytesByRemote.GetValueOrDefault(remote) + size;
    }

    private void PingLoop()
    {
        using var p1 = new Ping(); using var p2 = new Ping(); using var p3 = new Ping();
        int round = 0;
        while (!_stop)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            if (round++ % 10 == 0) PickServer();
            string? server; lock (_lock) server = _server;
            bool tryServer = server != null && (_serverAnswered || _serverFails < 5);
            var gwTask = _gateway != null ? Send(p1, _gateway, 500) : Task.FromResult<double?>(null);
            var netTask = Send(p2, Internet, 900);
            var srvTask = tryServer ? Send(p3, IPAddress.Parse(server!), 900) : Task.FromResult<double?>(null);
            try { Task.WaitAll(new Task[] { gwTask, netTask, srvTask }, 1000); } catch { }
            double? gw = gwTask.IsCompletedSuccessfully ? gwTask.Result : null;
            double? net = netTask.IsCompletedSuccessfully ? netTask.Result : null;
            double? srv = srvTask.IsCompletedSuccessfully ? srvTask.Result : null;
            lock (_lock)
            {
                _pingGw = gw; _pingNet = net; _lostNet = net == null; _pingServer = srv;
                if (_gateway != null) { _gwSent++; if (gw is double g) _gwAll.Add(g); else _gwLost++; }
                _netSent++; if (net is double n) _netAll.Add(n); else _netLost++;
                if (tryServer) { if (srv is double s) { _serverAnswered = true; _srvAll.Add(s); } else if (!_serverAnswered) _serverFails++; }
            }
            int wait = 1000 - (int)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    /// <summary>Servidor = endereço público com mais bytes trocados com o jogo desde o início.</summary>
    private void PickServer()
    {
        lock (_lock)
        {
            var top = _bytesByRemote.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (top.Key != null && top.Key != _server) { _server = top.Key; _serverFails = 0; _serverAnswered = false; }
        }
    }

    private static async Task<double?> Send(Ping p, IPAddress ip, int timeoutMs)
    {
        try
        {
            var r = await p.SendPingAsync(ip, timeoutMs);
            return r.Status == IPStatus.Success ? r.RoundtripTime : null;
        }
        catch { return null; }
    }

    public override void Fill(double?[] v, int o)
    {
        double secs = System.Diagnostics.Stopwatch.GetElapsedTime(_lastFill).TotalSeconds;
        _lastFill = System.Diagnostics.Stopwatch.GetTimestamp();
        long rx = Interlocked.Exchange(ref _rx, 0), tx = Interlocked.Exchange(ref _tx, 0);
        _rxTotal += rx; _txTotal += tx;
        lock (_lock)
        {
            v[o] = _pingGw; v[o + 1] = _pingNet; v[o + 2] = _lostNet ? 1 : 0; v[o + 3] = _pingServer;
        }
        if (_etw != null && secs > 0) { v[o + 4] = rx * 8 / 1000.0 / secs; v[o + 5] = tx * 8 / 1000.0 / secs; }
    }

    public override Dictionary<string, object?> Finish()
    {
        _stop = true;
        _pingThread?.Join(2500);
        try { _etw?.Source.StopProcessing(); } catch { }
        _etw?.Dispose(); _etw = null;
        lock (_lock)
        {
            return new()
            {
                ["rede"] = new
                {
                    roteador = Stats(_gwAll, _gwSent, _gwLost),
                    internet_1111 = Stats(_netAll, _netSent, _netLost),
                    servidor = _server,
                    servidor_responde_ping = _server == null ? (bool?)null : _serverAnswered,
                    servidor_ping = _srvAll.Count > 0 ? Stats(_srvAll, _srvAll.Count, 0) : null,
                    jogo_recebido_mb = Math.Round(_rxTotal / 1048576.0, 1),
                    jogo_enviado_mb = Math.Round(_txTotal / 1048576.0, 1),
                }
            };
        }
    }

    private static object? Stats(List<double> rtts, int sent, int lost) => sent == 0 ? null : new
    {
        medio_ms = rtts.Count > 0 ? Math.Round(rtts.Average(), 1) : (double?)null,
        max_ms = rtts.Count > 0 ? rtts.Max() : (double?)null,
        jitter_ms = rtts.Count > 1 ? Math.Round(rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(a - b)).Average(), 1) : (double?)null,
        perda_pct = Math.Round(100.0 * lost / sent, 1),
    };

    public override void Dispose()
    {
        _stop = true;
        try { _etw?.Source.StopProcessing(); } catch { }
        _etw?.Dispose(); _etw = null;
    }

    // ---- conversões do payload (o tipo que o TraceEvent devolve varia: IPAddress, string, int...) ----

    private static long ToLong(object? o) => o switch
    {
        int i => i, uint u => u, long l => l, ulong ul => (long)ul, short s => s, ushort us => us, _ => -1
    };

    public static string? Address(object? o) => o switch
    {
        IPAddress ip => ip.ToString(),
        string s when IPAddress.TryParse(s, out var ip) => ip.ToString(),
        uint u => new IPAddress(u).ToString(),   // IPv4 em ordem de rede, como vem no evento
        int i => new IPAddress((uint)i).ToString(),
        byte[] b when b.Length is 4 or 16 => new IPAddress(b).ToString(),
        _ => null
    };

    public static bool IsPublic(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC);
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 0 || b[0] >= 224 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }
}
