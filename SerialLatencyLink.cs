using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>Conversa com o testador Arduino pela porta COM (115200, uma linha por comando).</summary>
public sealed class SerialLatencyLink : ILatencyLink, IDisposable
{
    private readonly SerialPort _port;

    public SerialLatencyLink(string portName)
    {
        // Nunca abrir a 1200 baud: nas placas ATmega32u4 isso reinicia no modo de gravação.
        _port = new SerialPort(portName, 115200) { DtrEnable = true, RtsEnable = true, NewLine = "\n", WriteTimeout = 1000 };
        _port.Open();
        _port.DiscardInBuffer();
    }

    public Task<string?> CommandAsync(string line, int timeoutMs, CancellationToken ct) => Task.Run(() =>
    {
        _port.DiscardInBuffer();
        _port.Write(line + "\n");
        return Read(timeoutMs);
    }, ct);

    public void Send(string line) { _port.DiscardInBuffer(); _port.Write(line + "\n"); }

    public Task<string?> ReadLineAsync(int timeoutMs, CancellationToken ct) => Task.Run(() => Read(timeoutMs), ct);

    public void SendRaw(char c) => _port.Write(new[] { (byte)c }, 0, 1);

    private string? Read(int timeoutMs)
    {
        _port.ReadTimeout = timeoutMs;
        try { return _port.ReadLine().Trim(); }
        catch (TimeoutException) { return null; }
    }

    public void Dispose() { try { _port.Close(); } catch { } _port.Dispose(); }

    public sealed record PortInfo(string Port, string Name, bool LikelyArduino)
    {
        public override string ToString() => $"{Port} · {Name}";
    }

    /// <summary>Portas COM com o nome do dispositivo; marca as placas que podem ser o testador (Arduino/SparkFun/Adafruit com USB nativo).</summary>
    public static List<PortInfo> Ports()
    {
        var list = new List<PortInfo>();
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (ManagementObject o in q.Get())
                using (o)
                {
                    string name = o["Name"]?.ToString() ?? "", id = o["PNPDeviceID"]?.ToString() ?? "";
                    var m = Regex.Match(name, @"\((COM\d+)\)");
                    if (!m.Success) continue;
                    bool arduino = Regex.IsMatch(id, @"VID_(2341|2A03|1B4F|239A)", RegexOptions.IgnoreCase);
                    list.Add(new PortInfo(m.Groups[1].Value, name.Replace(m.Value, "").Trim(), arduino));
                }
        }
        catch { foreach (var p in SerialPort.GetPortNames()) list.Add(new PortInfo(p, "porta serial", false)); }
        return list.OrderByDescending(p => p.LikelyArduino).ThenBy(p => p.Port.Length).ThenBy(p => p.Port).ToList();
    }

    /// <summary>Procura o testador perguntando "PING" só às placas com cara de Arduino (outras placas seriais não recebem nada).</summary>
    public static async Task<string?> FindAsync(IEnumerable<PortInfo> ports, CancellationToken ct)
    {
        foreach (var p in ports.Where(x => x.LikelyArduino))
        {
            try
            {
                using var link = new SerialLatencyLink(p.Port);
                await Task.Delay(300, ct); // a placa termina de montar a serial USB depois de aberta
                if (LatencyTester.IsTester(await link.CommandAsync("PING", 800, ct))) return p.Port;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* porta ocupada (monitor serial aberto?) ou outra placa */ }
        }
        return null;
    }
}
