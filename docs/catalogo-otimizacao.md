# Catálogo de otimização para CS2 (11/11/2026)

Tudo que aparece em guias, fóruns e na documentação da Microsoft, AMD e NVIDIA, com o veredito para **este PC**
(Ryzen 7 5700X, 2x8 GB 3200, RTX 3060, MSI B550, dois monitores 200/144 Hz, FACEIT).

**Vereditos:**
- **JÁ TEM**: já está no app.
- **VALE**: ganho real ou limpeza sem risco; aplicar.
- **MEDIR**: base técnica, efeito varia de PC para PC; entra como passo da matriz e só fica se ganhar além do ruído.
- **LIMPEZA**: tira peso do Windows, mas não muda o fps de forma medível.
- **NÃO VALE**: placebo, sem prova, ou piora.
- **PROIBIDO**: quebra a FACEIT, a segurança ou as atualizações.

Regra que vale para tudo: o CS2 é limitado pela CPU nesse PC. O que ajuda é tirar trabalho da CPU e do sistema no instante do jogo,
não "liberar RAM" por liberar.

## 1. CS2

| Item | Veredito | Por quê |
| --- | --- | --- |
| Opções de inicialização mínimas | VALE | Um desenvolvedor da Valve disse que "as melhores opções de inicialização são nenhuma", porque são as mais testadas. Manter só `-condebug` (benchmark) e `+fps_max` se usado. |
| `-high` | NÃO VALE | O Modo CS automático já sobe a prioridade; a Valve desaconselha mexer em prioridade e threads à mão. |
| `-threads N` | NÃO VALE | Mesma fala da Valve: costuma piorar. |
| `-novid`, `-nojoy`, `-console` | NÃO VALE (inofensivo) | Sem efeito em fps. |
| `-tickrate`, `-limitvsconst` e outros do CS:GO | NÃO VALE | Não existem no CS2. |
| Reflex On + Boost | VALE | Mantém a GPU com clock alto mesmo quando a CPU é o limite e reduz a fila de frames. |
| `fps_max` × G-Sync + V-Sync + Reflex | MEDIR | Já está na matriz (passos 2 e 3). |
| Sombras, partículas, oclusão de ambiente, MSAA, shaders | MEDIR | Já são os "passos de vídeo" da matriz. Sombras dinâmicas mostram a sombra do inimigo: só tire se o ganho for grande. |
| Boost Player Contrast | MEDIR | Custo baixo segundo os guias; ajuda a ver o inimigo. |
| Cache de shaders | JÁ TEM | Aquecimento obrigatório depois de atualizar driver ou CS2. |
| Downloads da Steam durante o jogo | VALE | Steam › Configurações › Downloads: desmarcar "Permitir downloads durante o jogo". O app só orienta (chave não confirmada). |
| Overlay da Steam e aceleração por GPU nas páginas da Steam | VALE | Steam › Interface: desligar "Aceleração por GPU nas visualizações web". Overlay já está em Overlays e aceleração. |

## 2. NVIDIA

| Item | Veredito | Por quê |
| --- | --- | --- |
| Perfil do `cs2.exe` com energia em desempenho máximo | JÁ TEM | Grava na NVAPI, com conferência do nome. |
| Modo de baixa latência no driver | NÃO VALE com Reflex | O Reflex do jogo sobrepõe essa opção. |
| Tamanho do cache de shaders "Ilimitado" ou 10 GB | VALE | Evita recompilar shaders e as travadas que isso causa. É configuração global do painel; o app orienta (ID na NVAPI não confirmado). |
| Otimização de threads | NÃO VALE | Deixar em Automático. |
| G-Sync só tela cheia × janela | MEDIR | Depende do modo de tela que vencer no teste de alt-tab. |
| Filtro de textura "alto desempenho", anisotrópico | NÃO VALE | Efeito desprezível no CS2 com a 3060. |
| Driver limpo (DDU em modo seguro) | VALE quando houver problema | Só depois de atualização com travadas novas; roteiro, não automático. |
| MPO desligado (`OverlayTestMode = 5`) | MEDIR | A NVIDIA chegou a recomendar desligar o Multiplane Overlay para cintilação e travadas em janela e com vários monitores. Os builds novos do Windows podem ignorar a chave. |
| HAGS | MEDIR | Já está na matriz como experimento. |
| Modo MSI da GPU | JÁ TEM (leitura) | A NVIDIA já usa MSI na maioria das placas; trocar à mão tem risco. |

## 3. Processador, BIOS e RAM

| Item | Veredito | Por quê |
| --- | --- | --- |
| BIOS com AGESA 1.2.0.7 ou mais nova | VALE | Corrige as travadas periódicas do fTPM. Use versão estável, não beta. Já é o passo 1 da matriz e há aviso de BIOS antiga. |
| Driver de chipset da AMD atualizado | VALE | Traz o driver de gerenciamento de energia do Ryzen. O app passa a conferir a versão. |
| CPPC | NÃO VALE desligar | Sem teste confiável a favor; deixar ligado. |
| Preferred Cores | MEDIR | É o único dessa família que vale testar; passo da matriz com roteiro de BIOS. |
| C-States globais desligados / Power Supply Idle Control em "Typical" | MEDIR | Comum em fóruns de Ryzen para travadas; esquenta mais em repouso. Passo da matriz. |
| PBO + Curve Optimizer | JÁ TEM | Descoberta do CO na matriz. |
| RAM a 3600 e FCLK 1800 (1:1) | MEDIR | Já está na matriz com o roteiro de estabilidade. |
| Resizable BAR | MEDIR | Já está na matriz. |
| Desligar SVM/virtualização | PROIBIDO | O VBS e a FACEIT dependem dela. |

## 4. Windows: o que a própria Microsoft documenta

| Item | Veredito | Por quê |
| --- | --- | --- |
| Integridade de Memória (HVCI) desligada | MEDIR, só na descoberta | A Microsoft diz que pode reduzir o desempenho em jogos e sugere desligar só para jogar. A FACEIT avisa ou bloqueia; por isso fica só na fase de descoberta. |
| Plataforma de Máquina Virtual (VMP) desligada | VALE | A Microsoft diz que desligar não tem impacto de segurança; perde WSL2, Android e máquinas virtuais. Não desliga o VBS. |
| Hyper-V, Windows Sandbox, WSL (recursos opcionais) | VALE se não usa | Mesma lógica da VMP. |
| Modo de Jogo, Game DVR desligado, otimizações em janela | JÁ TEM | |
| "Não perturbe" automático ao jogar | VALE | Configurações › Sistema › Notificações. Notificação por cima do jogo tira o foco e pode causar alt-tab. |
| Plano de energia e núcleos sem estacionamento | JÁ TEM | "Desempenho Máximo" não muda nada em PC de mesa em relação ao Alto desempenho. |
| `GlobalTimerResolutionRequests = 1` (Windows 11) | MEDIR | Devolve o comportamento antigo do timer para o sistema todo. Há relatos dos dois lados e nenhum teste controlado; passo da matriz. |
| `disabledynamictick`, HPET (`useplatformclock`) | JÁ TEM | Experimento e diagnóstico. |
| `Win32PrioritySeparation` | JÁ TEM | Experimento. |
| `SystemResponsiveness` (MMCSS) | JÁ TEM | A Microsoft documenta valores entre 10 e 100; o app usa o padrão de jogos. |
| `NetworkThrottlingIndex` | NÃO VALE | Existe para proteger áudio, não para jogos; relatos contraditórios e nenhum teste. |
| Inicialização rápida (Fast Startup) desligada | VALE | Desligar faz cada "desligar" reiniciar o kernel e os drivers de verdade; tira estados velhos de driver. Custa alguns segundos no boot. |
| Otimização de Entrega (P2P das atualizações) | VALE | Desligar o envio para outros PCs tira upload e disco em segundo plano. |
| Defender com CPU limitada nas verificações | VALE | `ScanAvgCPULoadFactor` (5 a 100, padrão 50) para 25, e verificação agendada em horário fora do jogo. A Microsoft documenta. A proteção continua igual. |
| Exclusões do Defender nas pastas de jogo | PROIBIDO aqui | Já foi tirado do app por segurança. |
| Manutenção automática em horário fixo (madrugada) | VALE | Evita a manutenção do Windows começar no meio da partida. |
| Otimizar unidades (TRIM semanal do SSD) | NÃO mexer | O TRIM é necessário; a tarefa é leve. |
| Arquivo de paginação desligado ou fixo | NÃO VALE | Com 16 GB é perigoso: o jogo ou o Windows fecham quando a RAM acaba. |
| Compressão de memória desligada | NÃO VALE | Com 16 GB ela evita paginação. |
| Mitigações de Spectre/Meltdown | PROIBIDO | Já foi tirado do app. |

## 5. Windows: limpeza (o que os debloaters fazem)

O que Win11Debloat, Chris Titus WinUtil e O&O ShutUp10 fazem, separado em seguro e perigoso.

| Item | Veredito | Por quê |
| --- | --- | --- |
| Telemetria, Widgets, Copilot, dicas, apps promovidos | JÁ TEM | |
| Apps pré-instalados | JÁ TEM | Remoção com reinstalação pela Store. |
| Mais serviços seguros de desativar | LIMPEZA | Compartilhamento do Media Player, Hotspot móvel, Controle dos Pais, Cartão inteligente, Rastreamento de links distribuídos, AllJoyn, Programa Insider, Pagamentos e NFC, Carteira. Dezenas de MB de RAM no total. |
| Mais tarefas de telemetria | LIMPEZA | Feedback (DmClient), Family Safety, atualizações de mapas, CEIP restantes. |
| Edge: inicialização acelerada e execução em segundo plano | VALE | Políticas `StartupBoostEnabled = 0` e `BackgroundModeEnabled = 0`: o Edge deixa de ficar carregado na RAM sem estar aberto. |
| Pesquisa da web no Iniciar (Bing) | LIMPEZA | Política para desligar sugestões e pesquisa na web. |
| Atualização automática de apps da Store | LIMPEZA | Política para não baixar sozinha (você atualiza quando quiser). |
| Nível de telemetria mínimo | LIMPEZA | Política `AllowTelemetry`; no Windows Home o mínimo efetivo é "Necessário". |
| OneDrive desinstalado | VALE se não usa | Já sai da inicialização; desinstalar tira o processo e as tarefas de vez. Reinstalável. |
| Dispositivos não usados desabilitados (áudio onboard se usa headset USB, adaptadores de rede sem uso, Bluetooth) | VALE com diagnóstico | Menos interrupções e DPCs. Reversível pelo Gerenciador de Dispositivos; o app mostra e desabilita com restauração. |
| Razer Synapse fechado com o DPI gravado no mouse | MEDIR | Se o mouse tem memória interna, o DPI fica nele e o Synapse não precisa ficar aberto. O Synapse é pesado e cheio de serviços. |
| Desligar o Defender, a Store ou o Windows Update | PROIBIDO | Fazem parte dos debloaters "agressivos". Tiram segurança e atualização. |
| Windows modificados (Atlas, ReviOS, Tiny11) | PROIBIDO | Quebram atualização e anti-cheat. |

## 6. Rede

O CS2 manda pouco tráfego por UDP. Ajuste de rede não muda o fps; no máximo muda travadas de DPC da placa.

| Item | Veredito | Por quê |
| --- | --- | --- |
| Economia de energia da placa e EEE | JÁ TEM | |
| Moderação de interrupções da placa de rede | MEDIR | A Microsoft documenta que desligar reduz a latência e gasta mais CPU. Um teste de usuário achou o "Médio" melhor que desligado. Passo da matriz com o diagnóstico de DPC. |
| RSS | NÃO mexer | A Microsoft recomenda manter. |
| Controle de fluxo desligado | NÃO VALE | Sem medição. |
| Nagle, TcpAckFrequency, TCPNoDelay | NÃO VALE | O CS2 usa UDP. |
| QoS/DSCP para o cs2.exe | NÃO VALE | A maioria dos roteadores domésticos ignora. |
| Desligar IPv6, trocar DNS | NÃO VALE | Não afeta o jogo em si. |

## 7. Monitores

| Item | Veredito | Por quê |
| --- | --- | --- |
| Monitor do jogo a 200 Hz de verdade | JÁ TEM | Aviso de taxa abaixo do máximo. |
| Segundo monitor desligado, ou sem vídeo e animação durante o jogo | MEDIR | Dois monitores em taxas diferentes podem tirar o jogo do Independent Flip. Já existe a regra de diagnóstico; falta o passo da matriz. |
| Área de trabalho na resolução do jogo | JÁ TEM | Modo CS automático. |

## 8. Programas de "otimização"

| Item | Veredito | Por quê |
| --- | --- | --- |
| Limpadores de RAM que rodam o tempo todo | NÃO VALE | Esvaziam o cache e pioram. O app limpa só a lista de espera, antes de jogar. |
| Process Lasso, afinidade de núcleos | NÃO VALE | O 5700X tem um bloco de núcleos só. |
| "Game boosters" de terceiros | NÃO VALE | Fazem o que o app já faz, sem medir. |

## Fontes

- Valve, opções de inicialização (citação repetida em [refrag.gg](https://refrag.gg/blog/best-launch-options-for-cs2), [csmarketcap](https://csmarketcap.com/blog/tips-for-beginners/cs2-launch-options-best-settings-to-use-and-what-to-avoid), [xplay.gg](https://xplay.gg/blog/best-cs2-launch-options)).
- Vídeo e Reflex: [csdb.gg FPS guide](https://csdb.gg/guides/fps-optimization-guide), [csdb.gg vídeo](https://csdb.gg/guides/best-cs2-video-settings/), [totalcsgo](https://totalcsgo.com/guides/best-cs2-settings), [dexerto](https://dexerto.com/counter-strike-2/counter-strike-2-best-settings-cs2-settings-for-max-fps-competitive-advantages-2274866).
- NVIDIA: [esports.net](https://www.esports.net/news/counter-strike/best-cs2-nvidia-settings/), [Blur Busters](https://forums.blurbusters.com/viewtopic.php?p=102099), [Steam discussões](https://steamcommunity.com/app/730/discussions/0/3819662889082650572/?l=japanese).
- Microsoft (HVCI e VMP): [suporte Microsoft](https://support.microsoft.com/en-us/topic/a255f612-2949-4373-a566-ff6f3f474613), [Tom's Hardware](https://www.tomshardware.com/news/microsoft-windows-11-gaming-boost-tips), [TechSpot](https://www.techspot.com/news/96246-microsoft-releases-guide-optimize-windows-11-gaming-performance.html).
- Timer: [PCGamingWiki](https://community.pcgamingwiki.com/files/file/2563-global-timer-resolution-requests-windows-11), [BetterTimerResolution](https://github.com/plankeeee/BetterTimerResolution), [Blur Busters](https://forums.blurbusters.com/viewtopic.php?p=113547).
- MMCSS e NetworkThrottlingIndex: [Blur Busters](https://forums.blurbusters.com/viewtopic.php?p=117970), [SpeedGuide](https://www.speedguide.net/articles/-5812/p-0).
- AMD fTPM e CPPC: [Guru3D](https://www.guru3d.com/story/amd-agesa-v2-1-2-7-microcode-to-fix-ftpm-stutters/), [TechPowerUp](https://www.techpowerup.com/forums/threads/what-fixed-stuttering-and-random-framerate-spikes-in-games-for-me.327264), [HotHardware](https://hothardware.com/news/amd-bios-updates-resolve-ryzen-stuttering-issue).
- Debloaters: [TechSpot](https://www.techspot.com/article/3095-stop-windows-slop/), [Club386](https://www.club386.com/how-to-debloat-windows-11-stop-spying/), [tech2geek](https://www.tech2geek.net/best-windows-11-debloat-tools-remove-copilot-telemetry-and-bloatware/).
- Defender: [Microsoft Learn](https://learn.microsoft.com/en-us/defender-endpoint/schedule-antivirus-scans-powershell), [blog sobre ScanAvgCPULoadFactor](https://rahuljindalmyit.blogspot.com/2024/02/what-is-scanavgcpuloadfactor-in.html).
- Rede: [Microsoft Learn](https://learn.microsoft.com/en-us/windows-server/networking/technologies/network-subsystem/net-sub-performance-tuning-nics), [djdallmann GamingPCSetup](https://djdallmann.github.io/GamingPCSetup/CONTENT/RESEARCH/NETWORK), [Intel Community](https://community.intel.com/t5/Ethernet-Products/NIC-settings-for-low-latency-gaming/m-p/1484435).
