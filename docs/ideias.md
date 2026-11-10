# Ideias para o PC de CS2

Lista viva de melhorias, em ordem de quanto ajudam a achar o fps mais estável. Esforço: **P** (horas), **M** (um dia), **G** (vários dias).
Atualizada em 10/11/2026.

## Prioridade alta

| # | Ideia | Por que importa | Esforço |
| --- | --- | --- | --- |
| 1 | **Teste de ruído** — botão que roda 5 capturas da mesma configuração e diz se o benchmark é confiável (variação do 1% low abaixo de 5%) | Sem isso nenhum veredito vale. Hoje é preciso fazer na mão e olhar a faixa | P |
| 2 | **Matriz guiada** — a matriz do PRD vira um passo a passo: mostra o perfil atual, o próximo ajuste a testar, quantas execuções faltam, e quando um perfil vence ele vira a nova linha de base | Evita mudar duas coisas ao mesmo tempo e esquecer o que já foi testado | M |
| 3 | **Causa de cada queda, frame a frame** — no instante de cada queda, olhar no PresentMon se a CPU ou a GPU estava ocupada naquele frame (hoje o gargalo é da sessão inteira) | A queda de 400 para 150 pode ter causa diferente da média. É o que separa "CPU" de "driver/sistema" | M |
| 4 | **Thread principal do CS2** — uso de cada thread do jogo por ETW (troca de contexto), não dos núcleos | O uso por núcleo engana: o Windows move a thread entre núcleos. Ver a thread principal a 100% confirma o limite de CPU sem chute | G |
| 5 | **Paginação nas quedas** — páginas lidas do disco por segundo (contador `Memory\Pages Input/sec`) e lista em espera, cruzadas com as quedas | É o sinal direto da hipótese "16 GB não bastam". Hoje só há a RAM usada | P |
| 6 | **Veredito de compra** — painel com a regra do PRD aplicada aos dados: 5500X3D só se, depois dos ajustes grátis, o 1% low seguir abaixo de 200 fps com gargalo de CPU; 32 GB só se a RAM passar de 85% com os programas do dia a dia | Responde a pergunta original (comprar ou não) com números | P |

## Otimizador do Windows: o que falta

O que já existe: plano de energia, núcleos sem estacionamento ("unpark"), serviços desnecessários, inicialização só com o essencial, tarefas de
telemetria, Widgets/Copilot/dicas, Modo de Jogo, Game DVR, USB/PCIe/placa de rede sem economia, aceleração do mouse, MMCSS, limpeza da memória em
espera (como o ISLC), fechar programas e troca da área de trabalho para 1280x960 quando o jogo é aberto pelo JOGAR.

| # | Ideia | Por que importa | Esforço |
| --- | --- | --- | --- |
| 16 | **Modo CS automático** — quando o `cs2.exe` abre (de qualquer jeito, não só pelo JOGAR): troca a resolução da área de trabalho, fecha o que não está na lista permitida, limpa a RAM, para por enquanto SysMain, Windows Search, Spooler e Windows Update, sobe a prioridade; desfaz tudo quando o jogo fecha | É o "script pré-jogo" sem mexer no Windows de forma permanente. O que só pesa durante o jogo fica parado só durante o jogo | M |
| 17 | **Overlays e aceleração** — verificar e oferecer desligar: aceleração de hardware e overlay do Discord, overlay da Steam, overlay/gravação instantânea da NVIDIA, Xbox Game Bar | Esses roubam GPU, RAM e às vezes frames do jogo. Ganho real e medível, ao contrário da maioria dos ajustes de registro | P |
| 18 | **Remover apps pré-instalados (debloat)** — lista dos apps da Store que vêm com o Windows (Clipchamp, Notícias, Clima, Solitaire, Teams pessoal, Xbox, Dicas…) com remoção para o usuário e reinstalação pela Store | Libera disco e tira tarefas de segundo plano. Ganho de fps pequeno; vale pela limpeza | M |
| 19 | **Experimentos de sistema (medir antes de manter)** — `bcdedit /set disabledynamictick yes`, modo MSI da GPU, `Win32PrioritySeparation`. Cada um vira um perfil na matriz e só fica se ganhar além do ruído | São os únicos ajustes "de Instagram" com base técnica; o efeito varia de PC para PC | M |

**Não entram:** desligar VBS/Integridade de Memória, Defender ou mitigações de CPU (FACEIT exige ou é risco), Windows modificados tipo Atlas/ReviOS
(quebram atualização e anti-cheat), ajustes de rede como TcpAckFrequency/Nagle (o CS2 usa UDP), "limpadores de RAM" que esvaziam a memória dos
programas o tempo todo (pioram), e afinidade de núcleos no 5700X (um CCD só, sem ganho).

## Prioridade média

| # | Ideia | Por que importa | Esforço |
| --- | --- | --- | --- |
| 7 | **Início automático da passada do mapa** — detectar o começo do benchmark no `console.log` | Tira o passo manual. Depende de um `console.log` real de uma passada | P |
| 8 | **Alerta de linha de base vencida** — quando o CS2, o driver NVIDIA ou o Windows mudam de versão, avisar que a linha de base precisa ser medida de novo | O PRD diz que atualização invalida as comparações; hoje isso só aparece no diff | P |
| 9 | **Diagnóstico de interrupções** — ler (só ler) se GPU e placa de rede usam MSI, em qual núcleo caem as interrupções e se o HPET foi forçado (`bcdedit useplatformclock`) | Configuração errada aqui causa travadas periódicas; complementa o diagnóstico de DPC | M |
| 10 | **Clock efetivo e C-states por núcleo nas quedas** — o LibreHardwareMonitor já dá "Effective Clock" | Confirma ou descarta clock stretching do Curve Optimizer -20 com dado, não com regra | P |
| 11 | **Comparar versões do driver NVIDIA** — agrupar sessões por versão do driver e sugerir voltar à versão anterior se a nova for pior além do ruído | Driver novo às vezes piora o CS2 | P |
| 12 | **Exportar um perfil inteiro** — todas as execuções de um perfil num CSV (ou direto para o "Analisar com Claude") | Facilita análise fora do app | P |

## Interessante, não urgente

| # | Ideia | Por que importa | Esforço |
| --- | --- | --- | --- |
| 13 | **Configuração do CS2 sugerida** — ler o `cs2_video.txt` e marcar opções que custam CPU no CS2 (ex.: Boost Player Contrast, sombras dinâmicas) como experimentos da matriz | Ajustes de vídeo que pesam na CPU são candidatos grátis | P |
| 14 | **Histórico no tempo** — gráfico do 1% low da linha de base ao longo das semanas | Mostra se o PC está piorando (poeira, pasta térmica, Windows inchando) | P |
| 15 | **Teste de estabilidade da RAM a 3600** — roteiro guiado: aplicar na BIOS, rodar stress de RAM 30 min, conferir WHEA, só então medir | O experimento 6 do PRD tem risco; o roteiro evita medir com RAM instável | P |

## Feito

- Latência (até a tela, do PC, clique até a imagem) na comparação de perfis.
- Instalador, atualização automática e relatos de problema como issues.
