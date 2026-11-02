using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ResetExplode;

/// <summary>
/// "Fotografia" da configuração do PC no início de uma sessão de jogo (contexto.json), mais o que só dá para ver
/// durante o jogo (link PCIe sob carga). <see cref="SessionAdvice"/> transforma isso em avisos.
/// Só dados: a coleta no Windows fica em SessionContextCollector.
/// </summary>
public sealed class SessionContext
{
    public string? Cpu { get; set; }
    public string? Gpu { get; set; }
    public string? DriverGpu { get; set; }
    public string? Windows { get; set; }
    public List<ModuloRam> Ram { get; set; } = new();
    public bool? ResizableBar { get; set; }
    public uint? PcieMaxGeracao { get; set; }
    public uint? PcieMaxLargura { get; set; }
    /// <summary>Maior link PCIe visto durante o jogo (em repouso a placa baixa o link para economizar; sob carga deve chegar ao máximo).</summary>
    public uint? PcieGeracaoVista { get; set; }
    public uint? PcieLarguraVista { get; set; }
    public MonitorJogo? Monitor { get; set; }
    public bool? VrrWindows { get; set; }
    public string? PlanoEnergia { get; set; }
    public bool? Hags { get; set; }
    public bool? ModoJogo { get; set; }
    public List<string> AjustesLigados { get; set; } = new();
    public DiscoJogo? Disco { get; set; }
    /// <summary>Afinidade de núcleos do jogo ("todos" ou a lista), preenchida pela Fase 6.</summary>
    public string? Afinidade { get; set; }
    /// <summary>Último teste de atraso com o testador (Arduino ou câmera), se houver.</summary>
    public Dictionary<string, object?>? LatenciaMedida { get; set; }
    public List<string> ErrosColeta { get; set; } = new();

    public sealed class ModuloRam
    {
        public string Slot { get; set; } = "";
        public double CapacidadeGb { get; set; }
        public string Tipo { get; set; } = "";          // DDR4, DDR5, LPDDR5...
        public int VelocidadeNominalMts { get; set; }   // SMBIOS "Speed" (nem sempre é a do XMP)
        public int VelocidadeConfiguradaMts { get; set; }
        public string PartNumber { get; set; } = "";
        public bool Sodimm { get; set; }
    }

    public sealed class MonitorJogo
    {
        public string Nome { get; set; } = "";
        public int Largura { get; set; }
        public int Altura { get; set; }
        public int HzAtual { get; set; }
        /// <summary>Maior taxa suportada NESTA resolução.</summary>
        public int HzMaximo { get; set; }
        public int LarguraNativa { get; set; }
        public int AlturaNativa { get; set; }
        public string Conexao { get; set; } = ""; // HDMI, DisplayPort, DVI, VGA, Interna...
    }

    public sealed class DiscoJogo
    {
        public string Unidade { get; set; } = "";
        public string Tipo { get; set; } = "";   // SSD NVMe, SSD, HD, Externo (USB)...
        public string Modelo { get; set; } = "";
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Save(string folder) => File.WriteAllText(Path.Combine(folder, "contexto.json"), JsonSerializer.Serialize(this, Json));

    public static SessionContext? Load(string folder)
    {
        try { return JsonSerializer.Deserialize<SessionContext>(File.ReadAllText(Path.Combine(folder, "contexto.json")), Json); }
        catch { return null; }
    }
}

public enum AvisoNivel { Problema, Atencao, Dica }

public sealed record Aviso(AvisoNivel Nivel, string Texto)
{
    public string Marca => Nivel switch { AvisoNivel.Problema => "PROBLEMA", AvisoNivel.Atencao => "ATENÇÃO", _ => "DICA" };
    public override string ToString() => $"{Marca}: {Texto}";
}

/// <summary>Regras que viram avisos no cartão da sessão. Sem acesso ao sistema: recebe só o contexto.</summary>
public static class SessionAdvice
{
    public static List<Aviso> Evaluate(SessionContext c)
    {
        var list = new List<Aviso>();
        Ram(c, list);
        Gpu(c, list);
        Monitor(c, list);
        Windows(c, list);
        return list.OrderBy(a => a.Nivel).ToList();
    }

    private static void Ram(SessionContext c, List<Aviso> list)
    {
        var mods = c.Ram.Where(m => m.CapacidadeGb > 0).ToList();
        if (mods.Count == 0) return;
        bool soldered = mods.All(m => m.Tipo.StartsWith("LP", StringComparison.OrdinalIgnoreCase));
        bool notebook = mods.All(m => m.Sodimm) || soldered;
        int cfg = mods.Min(m => m.VelocidadeConfiguradaMts);
        string tipo = mods[0].Tipo;

        // 1) Velocidade: abaixo da nominal informada pelo pente, ou abaixo da que o código do pente indica (XMP/EXPO desligado).
        int rated = mods.Max(m => m.VelocidadeNominalMts);
        int fromPart = mods.Select(m => SpeedFromPartNumber(m.PartNumber, m.Tipo)).DefaultIfEmpty(0).Max();
        int target = Math.Max(rated, fromPart);
        if (cfg > 0 && !soldered)
        {
            if (target >= cfg * 1.1)
                list.Add(new(AvisoNivel.Problema,
                    $"RAM a {cfg} MT/s, mas os pentes são de {target} MT/s" + (fromPart > rated ? $" (pelo código {mods[0].PartNumber.Trim()})" : "") +
                    ". Ative o XMP/EXPO/DOCP na BIOS: em jogos que dependem de CPU isso costuma render 10–20% de FPS."));
            else if (!notebook && ((tipo == "DDR4" && cfg <= 2400) || (tipo == "DDR5" && cfg <= 4800)))
                list.Add(new(AvisoNivel.Atencao,
                    $"RAM {tipo} a {cfg} MT/s, a velocidade padrão de fábrica. Quase todo kit para jogos é mais rápido: confira se o XMP/EXPO está ligado na BIOS."));
        }

        // 2) Canais
        if (!soldered)
        {
            if (mods.Count == 1)
                list.Add(new(AvisoNivel.Problema,
                    $"Só um pente de RAM ({mods[0].CapacidadeGb:0} GB): a memória roda em canal único, com metade da banda. " +
                    "Com 2 pentes iguais (dual channel) jogos que dependem de CPU ganham bastante."));
            else if (mods.Count == 2 && Channel(mods[0].Slot) is char a && Channel(mods[1].Slot) is char b && a == b)
                list.Add(new(AvisoNivel.Problema,
                    $"Os 2 pentes estão no mesmo canal ({a}): mude um deles para um slot do outro canal (o manual da placa indica, normalmente A2 e B2) para ativar o dual channel."));
            if (mods.Select(m => m.CapacidadeGb).Distinct().Count() > 1)
                list.Add(new(AvisoNivel.Dica, "Pentes de RAM com tamanhos diferentes: o dual channel pode funcionar só em parte da memória."));
        }
    }

    private static void Gpu(SessionContext c, List<Aviso> list)
    {
        if (c.ResizableBar == false)
            list.Add(new(AvisoNivel.Dica,
                "Resizable BAR desligado. Se a placa de vídeo suporta (RTX 30/40/50, Radeon RX 6000+, Intel Arc), ligue na BIOS " +
                "\"Above 4G Decoding\" e \"Re-Size BAR\" (exige BIOS em modo UEFI, sem CSM): até ~10% de FPS em alguns jogos."));

        if (c.PcieMaxGeracao is uint maxG && c.PcieGeracaoVista is uint seenG && seenG < maxG)
            list.Add(new(AvisoNivel.Atencao,
                $"A placa de vídeo ficou em PCIe {seenG}.0 durante o jogo, mas o máximo deste PC é {maxG}.0. Confira na BIOS a opção do slot PCIe (deixe em Auto ou Gen{maxG}) e se a placa está bem encaixada."));
        if (c.PcieMaxLargura is uint maxW && c.PcieLarguraVista is uint seenW && seenW < maxW)
            list.Add(new(AvisoNivel.Problema,
                $"A placa de vídeo está usando x{seenW} linhas PCIe de x{maxW} possíveis. Causas comuns: placa no slot errado (use o primeiro slot, mais perto da CPU), " +
                "contato sujo ou um SSD M.2 dividindo as linhas com o slot da placa."));
    }

    private static void Monitor(SessionContext c, List<Aviso> list)
    {
        if (c.Monitor is not { } m || m.HzAtual <= 0) return;
        if (m.HzMaximo > m.HzAtual + 1)
            list.Add(new(AvisoNivel.Problema,
                $"Monitor a {m.HzAtual} Hz, mas ele aceita {m.HzMaximo} Hz em {m.Largura}×{m.Altura}. Mude em Configurações › Sistema › Vídeo › " +
                "Vídeo avançado › Taxa de atualização (e no jogo, se ele tiver a opção)."));
        if (m.Conexao == "HDMI" && (m.HzMaximo > m.HzAtual + 1 || m.HzMaximo <= 60))
            list.Add(new(AvisoNivel.Dica,
                "Monitor ligado por HDMI. Se ele tem DisplayPort, use DisplayPort: HDMI 2.0 limita 1440p a ~144 Hz e 4K a 60 Hz, e alguns monitores só ativam G-Sync/FreeSync por DisplayPort."));
        if (m.LarguraNativa > 0 && m.Largura > 0 && m.Largura < m.LarguraNativa)
            list.Add(new(AvisoNivel.Dica,
                $"Jogo em {m.Largura}×{m.Altura}, abaixo da resolução nativa do monitor ({m.LarguraNativa}×{m.AlturaNativa}): a imagem fica menos nítida. " +
                "Para ganhar FPS sem perder tanta nitidez, prefira DLSS/FSR na resolução nativa."));
        if (c.VrrWindows == false && m.HzMaximo > 60)
            list.Add(new(AvisoNivel.Dica, "\"Taxa de atualização variável\" do Windows desligada (Configurações › Vídeo › Gráficos): ligue para jogos em janela sem borda aproveitarem o G-Sync/FreeSync."));
    }

    private static void Windows(SessionContext c, List<Aviso> list)
    {
        if (c.Disco is { } d)
        {
            if (d.Tipo == "HD")
                list.Add(new(AvisoNivel.Problema,
                    $"Jogo instalado num HD ({d.Unidade} {d.Modelo}): carregamentos lentos e engasgos quando o jogo lê dados do disco. Mova para um SSD (Steam: Propriedades › Arquivos instalados › Mover)."));
            else if (d.Tipo.StartsWith("Externo"))
                list.Add(new(AvisoNivel.Atencao, $"Jogo rodando de um disco externo ({d.Unidade}): a conexão USB pode causar engasgos de carregamento."));
        }
        if (c.ModoJogo == false)
            list.Add(new(AvisoNivel.Dica, "Modo de Jogo do Windows desligado: ligue em RESET OTIMIZA (dá prioridade ao jogo e segura o Windows Update durante a partida)."));
        if (c.PlanoEnergia is string plan && !Regex.IsMatch(plan, "alto|high|máximo|ultimate|desempenho|performance", RegexOptions.IgnoreCase))
            list.Add(new(AvisoNivel.Dica, $"Plano de energia \"{plan}\". Em PC de mesa, o Alto desempenho (RESET OTIMIZA) costuma melhorar o 1% low."));
    }

    /// <summary>Canal do slot pelo nome que o BIOS dá (ex.: "P0 CHANNEL A", "ChannelA-DIMM1", "DIMM_B2").</summary>
    public static char? Channel(string slot)
    {
        var m = Regex.Match(slot, @"CHANNEL\s*-?\s*([A-H])\b|Channel([A-H])|DIMM[_\s-]?([A-H])\d", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string g = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        return char.ToUpperInvariant(g[0]);
    }

    /// <summary>Velocidade escrita no código do pente (KF3200C16D4 → 3200, F5-6000J3038F16G → 6000), dentro da faixa do tipo.</summary>
    public static int SpeedFromPartNumber(string part, string tipo)
    {
        (int lo, int hi) = tipo == "DDR5" ? (4800, 9000) : tipo == "DDR4" ? (2133, 5333) : (0, 0);
        if (hi == 0) return 0;
        return Regex.Matches(part ?? "", @"(?<!\d)(\d{4})(?!\d)").Select(m => int.Parse(m.Groups[1].Value))
                    .Where(v => v >= lo && v <= hi).DefaultIfEmpty(0).Max();
    }
}
