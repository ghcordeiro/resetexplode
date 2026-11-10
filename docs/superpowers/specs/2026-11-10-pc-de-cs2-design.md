# PC de CS2: medir, otimizar e enxugar

10 de novembro de 2026 · Guilherme Cordeiro

## Intenção

Transformar o Reset Explode numa ferramenta pessoal, para um único PC, com um único objetivo: CS2 com o maior fps estável possível. O problema é a oscilação entre 150 e mais de 400 fps; a meta é subir o 1% low, não a média.

O app passa a fazer três coisas: medir com uma carga repetível e dizer onde está o gargalo, deixar o Windows dedicado ao jogo, e perder tudo que não serve a isso.

Este documento parte do PRD "Medidor de estabilidade de FPS no CS2" e o adapta ao código que já existe (aba Sessões).

## O que o dono do PC disse

- A ferramenta é só para ele. Não precisa mais servir a outras pessoas.
- O PC é usado para CS2, alguns jogos leves (Esports Manager) e, às vezes, copiar arquivos de HDs antigos.
- Não podem parar de funcionar: Discord, internet, Steam e Razer Synapse (controla o DPI do mouse).
- Joga em 1280x960 esticado, com dois monitores: AOC de 200 Hz (jogo) e AOC G24P de 144 Hz.
- Joga FACEIT, então TPM 2.0, Secure Boot e VBS ficam ligados.
- O PC não usa Bluetooth.
- O testador de atraso com Arduino sai.
- Para testar, o app pode mudar as configurações de vídeo do CS2, menos a resolução.
- O alt-tab do jogo é muito lento, e isso incomoda.

Tudo que segue além disso é escolha do autor do documento, sujeita a correção.

## Objetivos

- Duas execuções na mesma configuração dão 1% low com menos de 5% de diferença.
- Cada ajuste (do Windows, do driver, do jogo, da BIOS) pode ser medido contra a linha de base, com veredito: melhor, pior ou dentro do ruído.
- O app aponta o gargalo de cada execução e sugere o próximo ajuste a testar, com a evidência que levou à sugestão.
- Um botão deixa o Windows dedicado ao CS2 e outro desfaz tudo.
- O app fica menor: só o que serve a medir, diagnosticar e otimizar este PC.

## Não-objetivos

- Alterar a BIOS. O app sugere (PBO, Curve Optimizer, XMP, AGESA); quem muda é o usuário.
- Linha de comando separada e banco SQLite, como no PRD. Botões na aba Sessões e as pastas de sessão já cumprem o papel.
- Amostragem de sensores a 250 ms. Fica em 1 s: consultas ao driver já causaram engasgos no 1% low do benchmark de GPU.
- Suportar outros PCs, placas AMD/Intel ou outros jogos no modo benchmark.

## O que seria um fracasso mesmo funcionando

- Quebrar o FACEIT Anti-Cheat ou qualquer coisa da lista "não pode parar".
- Mudança sem volta: remover pacotes do Windows, apagar serviços, desligar o Windows Update ou o Defender.
- Aplicar ajuste de fórum sem medição. Todo ajuste precisa ter efeito mensurável ou motivo claro; o que ficar dentro do ruído é revertido.
- Tocar o processo do `cs2.exe` (injeção, hook, leitura de memória).
- Publicar esta versão como release pública: o push na `main` dispara a release.

## Restrições

- O código é escrito num Mac sem .NET. Nada é compilado nem executado localmente; o dono testa no PC.
- Por isso o primeiro passo é um workflow de CI que só compila (`dotnet build` em `windows-latest`) a cada push no branch de trabalho, sem publicar.
- Todo o trabalho vai num branch `pc-de-cs2`, que não é mesclado na `main`.
- Os ajustes novos usam o mecanismo que já existe em `WindowsTweaks.cs` (`Tweak` com `Read`, `Write`, `Restore` e `TweakBackup`), que guarda o valor original.

## Parte 1: medir e diagnosticar

### Perfil e execução

- A aba Sessões ganha um campo **Perfil de teste** (texto livre, ex.: "linha de base", "fps_max 240"). O nome vai para o `resumo.json` e o `contexto.json` da sessão.
- Sessão com perfil nunca é apagada pela limpeza de 3 GB (`SessionStore.Prune`).
- Junto do perfil há um arquivo `perfil-manual.json` com o que o Windows não lê: PBO, Curve Optimizer, limites PPT/TDC/EDC, timings e tensões de RAM. O app abre o arquivo para edição e o copia para a pasta da sessão.

### Modo benchmark do CS2

- O botão **BENCHMARK CS2** recusa iniciar se achar o FACEIT Anti-Cheat em execução.
- O jogo é aberto pelo `GameLauncher` com `-condebug`. O app acompanha `game/csgo/console.log`.
- Cada passada do mapa de benchmark vira uma sessão própria (mesma pasta, mesmos arquivos de hoje), cortada entre o marcador de início e a linha `[VProf] FPS: Avg=…, P1=…`.
- O `P1` impresso pelo mapa é gravado no resumo ao lado do 1% low calculado. Diferença acima de 5% aparece como aviso.
- A primeira passada depois de abrir o jogo é marcada como aquecimento e fica fora da comparação.
- Captura manual por tempo fixo (atalho de início + duração). É entregue primeiro, porque ainda não há um `console.log` real para definir os marcadores; a detecção automática entra quando houver. Serve também para deathmatch offline com bots.
- O overlay fica desligado durante o benchmark.

### Validação

Uma execução é marcada como inválida, com o motivo, quando:

- houve alt-tab (já registrado em `eventos.csv`);
- o contexto lido no fim difere do lido no início;
- o PresentMon perdeu eventos;
- o FACEIT Anti-Cheat apareceu durante a captura.

### Métricas novas

- Desvio padrão do frametime.
- **Quedas**: quadros acima de 2,5x a mediana móvel de 1 s, sem o piso de 33,4 ms. O "engasgo" atual (3x e no mínimo 33,4 ms) continua existindo; a 300 fps ele não enxerga uma queda para 100 fps.
- Periodicidade das quedas (intervalo regular entre elas), para o diagnóstico de fTPM.
- Carga e clock de cada núcleo em `sensores.csv` (confirmar se já estão; se não, acrescentar).

### Snapshot ampliado

Passam a ser gravados em `contexto.json`:

| Dado | Origem |
| --- | --- |
| Versão e data da BIOS, placa-mãe | WMI (já lido em `SystemInfoCollector.cs`) |
| VBS e Integridade de Memória | Registro (`DeviceGuard`, caminhos já em `WindowsTweaks.cs`) |
| Secure Boot | Registro (`SecureBoot\State`) |
| TPM | WMI (`Win32_Tpm`) |
| Versão do CS2 | `buildid` do `appmanifest_730.acf` |
| Vídeo do CS2 | `cs2_video.txt` (valores principais) |
| `fps_max` e opções de inicialização | cfg do jogo e `localconfig.vdf` |
| Perfil NVIDIA do `cs2.exe` | NVAPI (`NvidiaProfiles.cs`): limite de fps, baixa latência, V-Sync, G-Sync, modo de energia |
| Todos os monitores | Resolução, Hz, qual é o principal |
| Programas abertos | Razer Synapse, Discord e os demais |
| Ajustes do "PC de CS" ligados | Lista da Parte 2 |

### Comparação por perfil

- `Compare` passa a aceitar perfis, não só duas sessões. Para cada métrica mostra mediana e faixa (mínimo e máximo) das execuções válidas.
- Veredito: a diferença entre as medianas só vale quando é maior que a maior faixa dos dois perfis. Caso contrário, "dentro do ruído".
- Perfil com menos de 3 execuções válidas é mostrado com aviso, sem veredito.
- O diff de configuração entre os perfis usa o snapshot ampliado.

### Diagnóstico e sugestões

Cada regra produz uma linha no laudo com evidência, sugestão e custo (zero ou compra). A sugestão é sempre um experimento a medir, nunca uma promessa.

| Sinal medido | Diagnóstico | Sugestão |
| --- | --- | --- |
| GPU abaixo de 90% e um núcleo acima de 90% nos segundos de fps baixo | Limite de CPU em poucos threads | Limite de fps; fechar programas; RAM mais rápida. Se o 1% low seguir abaixo de 200 fps: 5500X3D |
| RAM acima de 85% ou commit perto do limite, com quedas junto de paginação | Memória cheia | Medir com os programas do dia a dia; 32 GB |
| RAM a 3200 CL16 em Ryzen 5000 e gargalo de CPU | Latência de RAM | Testar 3600 com timings ajustados, validado em teste de memória |
| Quedas em intervalo regular e BIOS antiga | Stutter do fTPM | Atualizar a BIOS |
| Fps mediano muito acima da taxa do monitor e frametime muito variável | Fps sem limite | Comparar `fps_max` contra G-Sync + V-Sync + Reflex |
| Clock efetivo cai com núcleo em carga alta, sem temperatura alta | Curve Optimizer agressivo | Testar CO -15 e boost +100 MHz |
| Segundo monitor ativo em outra taxa e o jogo fora de "Independent Flip" (coluna do PresentMon) | Compositor do Windows no caminho | Medir com o segundo monitor desligado; nada animado nele; comparar os dois modos de tela (ver "Alt-tab lento") |
| Programa em segundo plano no instante da queda | Interferência | Fechar ou tirar da inicialização (já existe) |
| PerfCap térmico ou de energia | Limitador | Já existe |

**Diagnóstico profundo (opcional, por último).** Uma execução à parte que liga eventos de DPC e ISR do kernel (ETW) e aponta qual driver atrasou o sistema no instante de cada queda. Custa desempenho, então essa execução nunca entra na comparação.

### Alt-tab lento

Causa provável: tela cheia exclusiva em 1280x960 com a área de trabalho em 1920x1080. Cada alt-tab troca o modo de vídeo do monitor, e os dois monitores piscam.

Experimento, medido como qualquer outro perfil:

- **Perfil A (hoje):** tela cheia exclusiva, esticado.
- **Perfil B:** janela sem borda, com o app trocando a área de trabalho do monitor do jogo para 1280x960 esticado ao abrir o CS2 e voltando ao fechar. O alt-tab deixa de trocar o modo de vídeo. A troca de resolução entra no perfil temporário do `GameLauncher`, que já desfaz ajustes quando o jogo fecha.

O B só vence se o jogo ficar em "Independent Flip" e o 1% low e a latência até a tela ficarem dentro do ruído do A. Efeito colateral do B: a área de trabalho do monitor principal fica em 4:3 enquanto o jogo está aberto; o segundo monitor não muda.

O tempo do alt-tab é medido pelo app: do instante em que o jogo perde o foco até voltar a entregar quadros no ritmo normal.

### Configurações de vídeo do CS2

O app pode gravar `cs2_video.txt` para testar um perfil, sempre com backup e botão de restaurar. Nunca altera resolução, proporção nem modo esticado. Cada mudança de vídeo é um perfil medido contra a linha de base, uma opção por vez.

### Relatório

Um arquivo HTML único, sem rede, no estilo do `ReportBuilder.cs`: gráfico de frametime ao longo do tempo, histograma, tabela comparativa dos perfis e diff de configuração. Gráficos em SVG embutido.

## Parte 2: otimizador "PC de CS"

Uma página em RESET FERRAMENTAS › Otimização com **APLICAR TUDO** e **RESTAURAR TUDO**. Cada item também tem interruptor próprio. Antes da primeira aplicação o app cria um ponto de restauração do Windows.

### O que é desligado

- **Serviços** (desativados, com o valor original guardado): SysMain, Windows Search, Spooler de impressão, Fax, telemetria (DiagTrack, dmwappushservice), Mapas, serviços do Xbox, Relatório de erros, RetailDemo, Telefone, Geolocalização, Registro remoto, Bluetooth.
- **Recursos do Windows**: Widgets, Copilot, sugestões e dicas, apps da Store em segundo plano, Game DVR, início rápido do Edge, efeitos visuais (animações e transparência).
- **Tarefas agendadas** de telemetria (Compatibility Appraiser, CEIP).
- **Inicialização**: tudo desativado, menos uma lista permitida: Steam, Discord, Razer Synapse, driver de áudio e da NVIDIA. Usa `StartupManager.SetEnabled`, que é reversível.

### O que é ajustado

- **Energia**: plano Desempenho Máximo, núcleos sem estacionamento, USB sem suspensão seletiva, PCIe sem economia.
- **Placa de rede**: sem economia de energia (Energy Efficient Ethernet, "permitir desligar o dispositivo").
- **Windows Update**: horário ativo no máximo e sem reinício automático com usuário logado. As atualizações continuam.
- **Perfil NVIDIA do `cs2.exe`**: energia em desempenho máximo, cache de shaders sem limite, filtro de textura em alto desempenho. Limite de fps, V-Sync, G-Sync e baixa latência seguem o perfil vencedor da medição.
- **CS2**: o app mostra a configuração de vídeo e as opções de inicialização atuais ao lado das recomendadas. Grava `fps_max`, opções de inicialização e `cs2_video.txt` (com backup); a resolução esticada fica como está.

### Antes de jogar

O botão **PREPARAR PARA JOGAR** fecha o que não está na lista permitida e limpa a memória em espera (já existe em `Optimizer.cs`), e confere se Steam, Discord e Synapse estão de pé.

### O que não é tocado

Áudio, rede (DHCP, DNS), serviço da Steam, serviços da Razer, Defender, Windows Update, TPM, Secure Boot, VBS, drivers de disco e USB.

Os três ajustes "REDUZ SEGURANÇA" que já existem saem do app: desligar VBS quebra o FACEIT, e os outros dois não combinam com um PC que joga online.

Ficam de fora os ajustes de rede de fórum (TcpAckFrequency e parecidos) e a resolução do timer: sem evidência de ganho.

## Parte 3: enxugar o app

| Sai | Arquivos principais |
| --- | --- |
| RESET MAKER inteiro: Portas COM, Drivers seriais e Atraso (Arduino) | `UsbCleaner.cs`, `MakerDrivers.cs`, `MainWindow.Maker.cs`, `LatencyTester.cs`, `LatencyWindow.cs`, `SerialLatencyLink.cs`, `MainWindow.Latency.cs`, pastas `Drivers/` e `Arduino/`, campo `LatenciaMedida` do contexto |
| RESET INTEGRAÇÃO (luzes) | `LightBackends.cs`, `CorsairSdk*.cs`, `MainWindow.Integ.cs` |
| Instalar apps | `AppInstaller.cs`, `MainWindow.Apps.cs` |
| FPS estimado | `GameCatalog.cs`, `HowManyFpsClient.cs`, `GameRow.cs` |
| Bateria | `BatteryInfo.cs` (o cartão de plano de energia fica) |
| Benchmarks sintéticos | `BenchRenderer.cs`, `GpuGameBench.cs`, `GpuRtBench.cs`, `MainWindow.Rt.cs`, `GameSimBench.cs`, `DiskBench.cs` |
| Teste de refrigeração | `CoolingTest.cs`, `MainWindow.Cooling.cs` |
| Ajustes "REDUZ SEGURANÇA" | Trecho de `WindowsTweaks.cs` |

Saem junto os pacotes `Vortice.Direct3D12` e `Vortice.Dxc`, e os trechos correspondentes de `MainWindow.xaml`, `MainWindow.xaml.cs`, `MainWindow.Dash.cs`, `README.md` e `ResetExplode.csproj`.

**Fica**, por servir ao objetivo:

- Sessões, Gargalo, Overlay, Sensores, Placa de vídeo, Sistema.
- Otimização, Inicialização, Drivers (versão do driver NVIDIA e da BIOS).
- Stress test, verificação de overclock e o teste de memória (`MemoryBench.cs`): validam Curve Optimizer e RAM a 3600.
- Discos e saúde dos discos: servem aos HDs antigos.
- Rede: internet é requisito do jogo.
- Biblioteca e inicializador de jogos, Analisar com Claude.

## Ordem de entrega

0. Branch `pc-de-cs2` e CI que só compila.
1. Parte 1, nesta ordem: perfil e fixar sessão; modo benchmark e validação; comparação por perfil; métricas novas; snapshot ampliado; regras de diagnóstico; relatório HTML; diagnóstico profundo.
2. Parte 2.
3. Parte 3.

A matriz de experimentos do PRD pode começar quando a comparação por perfil estiver pronta.

## Deixado para quem constrói

- IDs das configurações do driver NVIDIA (tabela `NvApiDriverSettings.h`).
- Nomes exatos dos serviços e das tarefas agendadas no Windows 11 atual, e o que fazer quando um deles não existe.
- Layout das telas novas, seguindo o tema existente.
- Limiar de "intervalo regular" e de "clock efetivo cai" nas regras de diagnóstico, calibrados com as primeiras sessões reais.

## Questões em aberto

- Texto exato das linhas de início e fim no `console.log`, e se o jogo grava em tempo real. Precisa de um `console.log` de uma passada real.

Enquanto isso não for respondido, vale a captura manual por tempo fixo.

**Mapa de benchmark:** "CS2 FPS BENCHMARK DUST2" (oficina, id 3240880604). A descrição dá como exemplo de saída `[VProf] FPS: Avg=517.3, P1=183.5`; comentários de usuários mostram antes dela `[VProf] -- Performance report --` e `[VProf] Summary of ... frames ...`. A página não diz como a passada começa nem quanto dura. O dono confirmou que o mapa funciona no PC dele.
