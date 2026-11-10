# RESET EXPLODE · PC de CS2

> Versão **pessoal**, só para CS2 neste PC (branch `pc-de-cs2`). Não faz parte da release pública e não deve ser mesclada na `main`
> (o push na `main` publica a release). Licença [MIT](LICENSE).

App WPF (.NET 8) com:
- **Sessões e Benchmark CS2** (RESET JOGOS › Sessões): grava sozinho cada jogo em tela cheia (frames, sensores, eventos, PresentMon) com laudo e sugestões; e o
  **benchmark do CS2** com perfis, captura armada, validação, comparação com o ruído medido e relatório HTML. Veja [NOVIDADES.md](NOVIDADES.md).
- **Otimização** (RESET OTIMIZA): limpeza de memória, fechar programas, ajustes do Windows para jogos e o perfil **PC de CS** (serviços, rede, Windows Update,
  inicialização, tarefas) com ponto de restauração e restauração dos originais. Cache de shaders (`ShaderCache.cs`).
- **Biblioteca** (RESET JOGOS): detecta jogos (Steam, Epic, GOG, Ubisoft), perfil por jogo (prioridade, plano de energia, GPU, núcleos da CPU, argumentos de inicialização,
  área de trabalho 1280x960 durante o jogo) e perfil do driver NVIDIA (limite de fps, baixa latência, energia, V-Sync).
- **Gargalo e Overlay**: equilíbrio CPU × GPU, leitura ao vivo e overlay (FPS, frametime, 1% low, sensores) medido por ETW sem injetar nada no jogo.
- **Sistema, Sensores, Placa de vídeo, Discos (S.M.A.R.T.), Rede, Drivers, Inicialização**.
- **Stress test** (CPU/RAM/GPU), **Overclock** (limite de energia/clock NVIDIA e verificação de estabilidade) e **Memória** (latência, banda e carregamento).
- **Relatório exportável** (HTML/JSON) do sistema.
- **Instalador e atualização automática** (`installer/ResetExplode.iss`, `Updater.cs`): cada push no `pc-de-cs2` publica a pre-release `cs2-N`; o app se atualiza sozinho.
- **Relatar problema** (`ErrorReporter.cs`, `ProblemReportWindow.cs`): erros e relatos viram issues `[relato]` neste repositório.
  No Mac: `gh issue list -R ghcordeiro/resetexplode --search "[relato] in:title"`.

## Interface
- Visual preto e branco minimalista em **blocos** (cartões arredondados, inspirado em painéis de dashboard). Um único menu lateral
  com *Visão geral* no topo e categorias **RESET** recolhíveis (começam fechadas; clique para ver as sub-abas):
  - *Visão geral* (`MainWindow.Dash.cs`): gráfico de área CPU × GPU (temperatura, carga ou consumo; 1/5/15 min),
    última captura do benchmark do CS2, tabela de componentes, anéis de RAM/VRAM/disco, eventos recentes e saúde do PC.
  - **RESET EXPLODE** · Sistema, Sensores, Placa de vídeo, Discos, Rede.
  - **RESET JOGOS** · Biblioteca, Gargalo, Overlay, Sessões.
  - **RESET DESEMPENHO** · Memória, Stress test, Overclock.
  - **RESET FERRAMENTAS** · Otimização, Inicialização, Drivers.
- Botão de **modo escuro/claro** no canto superior direito (`ThemeManager.cs`); a escolha fica salva. O overlay de jogos e os avisos de dispositivo ficam sempre escuros.
- Leitura ao vivo de temperatura e carga da CPU e da GPU sempre visível no rodapé do menu.
- Sensores com busca e filtros por tipo (chips); tema em `Theme.xaml`.

## Base utilizada
- Interface, stress test e gráficos: código próprio.
- Sensores: [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL 2.0) — mantenha o aviso de licença ao distribuir.
- Informações do sistema: WMI do Windows.
- Medição de FPS: [TraceEvent](https://github.com/microsoft/perfview) (MIT), da Microsoft, para ler eventos ETW.
- Sessões de jogo: [PresentMon](https://github.com/GameTechDev/PresentMon) 2.6.0 (MIT), da Intel, embutido em `Tools/` (veja `Tools/LEIA-ME.md`).
- Renderização do stress de GPU: [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) (MIT), bindings de Direct3D 11 para .NET.

## Requisitos
- Windows 10/11
- .NET 8 SDK (https://dotnet.microsoft.com/download)
- Driver **PawnIO** (temperatura/consumo da CPU): `winget install namazso.PawnIO`
  A LibreHardwareMonitorLib 0.9.6+ usa o PawnIO no lugar do antigo WinRing0, que o Windows Defender bloqueia.

## Como rodar
```
cd ResetExplode
dotnet run
```
O app pede privilégio de administrador (necessário para sensores de baixo nível).
Para levar a outro PC: dê dois cliques em `publicar.bat`. Ele gera `..\ResetExplode-dist\ResetExplode.exe` (arquivo único, com o .NET embutido,
sem caminhos do seu usuário) + `LEIA-ME.txt`. No outro PC só é preciso instalar o PawnIO. Rode de novo após cada alteração no código.

### Baixar a versão mais recente
A cada push na `main`, o GitHub Actions (`.github/workflows/publicar.yml`) gera o `ResetExplode.exe` e atualiza a release
**latest** (aba *Releases* do repositório), com o `.exe` e um `.zip`. É só baixar de lá, sem compilar.
Ao criar uma tag (`git tag v0.12` e `git push --tags`), também sai uma release fixa dessa versão.

## Observações
- Se algum sensor não aparecer, depende do suporte da biblioteca à sua placa-mãe.

## Licença

MIT — veja o arquivo [LICENSE](LICENSE).
