using System.IO;
using System.Text;
using System.Windows;

namespace ResetExplode;

/// <summary>
/// ANALISAR COM CLAUDE: grava uma fotografia do PC (relatório, sensores, registro dos testes) numa pasta e abre o Claude Code
/// nela, num terminal SEM administrador. O login é feito pelo próprio Claude Code, com a conta do usuário; o app não vê a conta.
/// </summary>
public partial class MainWindow
{
    private static string ClaudeFolder => Path.Combine(AppSettings.Folder, "claude");

    private async void BtnClaude_Click(object sender, RoutedEventArgs e)
    {
        BtnClaude.IsEnabled = false;
        try { await OpenClaudeAsync(null); }
        finally { BtnClaude.IsEnabled = true; }
    }

    /// <param name="session">Pasta de uma sessão de jogo gravada (copiada para claude\sessao), ou null para só o PC e os testes.</param>
    /// <param name="previous">Sessão anterior do mesmo jogo, para comparar (copiada para claude\sessao_anterior).</param>
    private async Task OpenClaudeAsync(string? session, string? previous = null)
    {
        var ok = MessageBox.Show(this,
            "O RESET EXPLODE vai salvar os dados do PC (sistema, sensores, discos e o registro dos testes)" +
            (session != null ? (previous != null ? " e as duas sessões de jogo comparadas" : " e a sessão de jogo escolhida") : "") + " e abrir o Claude Code " +
            "num terminal para a IA analisar. Os dados só são enviados à Anthropic se você usar o Claude Code; números de série, " +
            "nome do computador e nome de usuário ficam de fora.\n\nNa primeira vez, faça o login com a sua conta do Claude no terminal.\n\nContinuar?",
            "Analisar com Claude", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) return;

        try
        {
            Directory.CreateDirectory(ClaudeFolder);
            CopySession(session, Path.Combine(ClaudeFolder, "sessao"));
            CopySession(session != null ? previous : null, Path.Combine(ClaudeFolder, "sessao_anterior"));
            var data = await BuildReportDataAsync("PC");
            // shortcut: troca por texto o nome do PC e do usuário (aparece em caminhos como C:\Users\<nome>); trocar por filtro por campo se aparecerem outros dados pessoais.
            string json = Anonymize(ReportBuilder.Json(data));
            string log = Anonymize(string.Join(Environment.NewLine, StressLog.Items.Cast<object>()));

            var utf8 = new UTF8Encoding(false);
            await File.WriteAllTextAsync(Path.Combine(ClaudeFolder, "relatorio.json"), json, utf8);
            await File.WriteAllTextAsync(Path.Combine(ClaudeFolder, "registro-testes.txt"), log.Length > 0 ? log : "(nenhum teste rodado nesta sessão)", utf8);
            await File.WriteAllTextAsync(Path.Combine(ClaudeFolder, "CLAUDE.md"), Instructions, utf8);
            string script = Path.Combine(ClaudeFolder, "abrir-claude.cmd");
            string prompt = session == null
                ? "Analise os dados deste PC seguindo o CLAUDE.md. Comece pelo que parece errado."
                : previous == null
                    ? "Analise a sessao de jogo da pasta sessao seguindo o CLAUDE.md. Comece pelo que atrapalhou o desempenho."
                    : "Compare a sessao de jogo da pasta sessao_anterior com a da pasta sessao seguindo o CLAUDE.md. Diga o que melhorou, o que piorou e por que.";
            await File.WriteAllTextAsync(script, LauncherScript.Replace("{PROMPT}", prompt).ReplaceLineEndings("\r\n"), utf8); // cmd.exe espera CRLF

            // Sem administrador: o Claude Code roda comandos, e não deve herdar o privilégio do RESET EXPLODE.
            ShellRunner.Run(script, "", ClaudeFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível abrir o Claude Code:\n" + ex.Message, "Analisar com Claude", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Substitui a pasta de destino pela cópia da sessão (ou só apaga, se não houver sessão).</summary>
    private static void CopySession(string? from, string to)
    {
        if (Directory.Exists(to)) Directory.Delete(to, true);
        if (from == null) return;
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }

    private static string Anonymize(string text)
    {
        foreach (var secret in new[] { Environment.MachineName, Environment.UserName }.Where(s => s.Length >= 3))
            text = text.Replace(secret, secret == Environment.UserName ? "usuario" : "PC", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    private const string LauncherScript = """
        @echo off
        chcp 65001 >nul
        cd /d "%~dp0"
        title RESET EXPLODE - Claude Code
        rem O instalador oficial poe o claude.exe em %USERPROFILE%\.local\bin e nao mexe no PATH: procura nos dois.
        set "CLAUDE="
        for /f "delims=" %%i in ('where claude 2^>nul') do if not defined CLAUDE set "CLAUDE=%%i"
        if not defined CLAUDE if exist "%USERPROFILE%\.local\bin\claude.exe" set "CLAUDE=%USERPROFILE%\.local\bin\claude.exe"
        if not defined CLAUDE (
          echo.
          echo  O Claude Code nao esta instalado neste PC.
          echo  Abra o PowerShell ^(sem ser administrador^) e rode:
          echo.
          echo      irm https://claude.ai/install.ps1 ^| iex
          echo.
          echo  Depois feche esta janela e clique de novo em ANALISAR COM CLAUDE.
          echo.
          pause
          exit /b
        )
        "%CLAUDE%" "{PROMPT}"
        """;

    private const string Instructions = """
        # Análise do PC (RESET EXPLODE)

        Você está ajudando o dono deste PC a entender o estado do hardware. Os dados foram exportados pelo RESET EXPLODE
        (app de diagnóstico para Windows) agora mesmo.

        ## Arquivos
        - `relatorio.json`: sistema (CPU, placa-mãe, RAM por módulo, Windows), placas de vídeo, discos com S.M.A.R.T.,
          energia, rede (se o teste foi rodado), todos os sensores no momento da exportação (valor, mínimo e máximo
          desde que o app abriu) e resultados de benchmark.
        - `registro-testes.txt`: registro do stress test e do teste de refrigeração desta sessão, com os vereditos.

        - `sessao/` (só quando o usuário escolheu uma sessão de jogo na aba Sessões):
          - `laudo.txt`: diagnóstico automático do RESET EXPLODE (sem IA). Comece por ele e confira com os dados.
          - `resumo.json`: jogo, duração, FPS médio, 1% e 0,1% low, percentis de frametime, engasgos, % do tempo por limitador,
            temperaturas máximas, erros de hardware, "exibicao" (PresentMon), "rede", "mouse", processos mais pesados, avisos e "laudo".
          - `contexto.json`: configuração no início da sessão (RAM por pente, XMP, PCIe, ReBAR, monitor/Hz/conexão, plano de energia,
            ajustes ligados do RESET OTIMIZA, disco do jogo, último teste de mouse/teclado).
          - `sensores.csv`: 1 linha por segundo (t_s): FPS, limitador, P-state/PerfCap da GPU, CPU/RAM/E/S/threads do jogo, GPU e VRAM
            do jogo e dos outros programas, pings (roteador, 1.1.1.1, servidor do jogo), tráfego do jogo, mouse_hz e TODOS os sensores
            (colunas "Hardware | Sensor (unidade)").
          - `exibicao.csv`: 1 linha por segundo do PresentMon (mesmo t_s): FPS exibido, descartados, latência até a tela, latência do
            PC (Reflex), clique até a imagem, CPU/GPU ocupadas por frame, gargalo (GPU/CPU/Trava), modo de apresentação, tearing.
          - `frames.csv`: TODO frame (t_ms, frametime_ms). `presentmon.csv.gz`: TODO frame com todas as colunas do PresentMon
            (comprimido). Podem ter milhões de linhas: use um script (PowerShell ou Python) para calcular, não leia inteiro.
          - `eventos.csv`: engasgos, alt-tab (segundo_plano/primeiro_plano), limitador térmico da GPU, CPU acima de 95 °C.
          - `processos.csv`: a cada 5 s, os programas que mais usaram CPU/GPU fora do jogo.
          Cruze os momentos ruins (engasgos, quedas de FPS) com o que os sensores e processos mostravam naquele segundo.
        - `sessao_anterior/` (só em comparações): a sessão anterior do mesmo jogo, com os mesmos arquivos. Compare os números e
          o que mudou entre os dois `contexto.json` (ajustes, driver, Hz, RAM) para dizer o que causou a diferença. Sessões de
          durações ou trechos muito diferentes do jogo não são comparáveis: avise.

        ## Como responder
        - Português do Brasil, linguagem simples; o usuário não é técnico.
        - Diga primeiro o que está errado ou em risco, com o número que mostra isso (ex.: "CPU a 88 °C com 76 W").
        - Compare com o normal para AQUELE hardware (modelo de CPU, GPU, cooler) e diga a causa provável.
        - Dê os passos para resolver em ordem, do mais simples ao mais trabalhoso. Quando houver uma aba do RESET EXPLODE
          que faz aquilo (Otimização, Drivers, Overclock, Teste de refrigeração...), indique a aba.
        - Se faltar um dado para concluir, diga qual teste do RESET EXPLODE rodar.

        ## Regras
        - Só análise. Não rode comandos que alterem o sistema (registro, drivers, serviços, BIOS, arquivos fora desta pasta).
          Se uma correção exigir isso, explique o passo para o usuário fazer.
        - Nunca sugira desligar Defender, UAC ou atualizações sem explicar o risco.
        """;
}
