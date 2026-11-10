# Plano: otimização total do Windows para CS2

> Fonte: `docs/catalogo-otimizacao.md`. Mesmas regras dos planos anteriores: build sem erros com `build.sh`, commit no `pc-de-cs2`, push por fase,
> sem testes unitários, backup gravado em disco antes de cada mudança e restauração. Nada marcado PROIBIDO no catálogo entra.

**Objetivo:** aplicar tudo que o catálogo marcou como VALE e LIMPEZA, transformar cada MEDIR em passo da matriz, e deixar o resto documentado
como "não fazer", para o Windows ficar só com o necessário sem perder segurança, FACEIT nem atualizações.

## Fase 1: VALE e LIMPEZA (ajustes com interruptor e restauração)

Novos `Tweak` em `CsProfile.cs` (entram no APLICAR PC DE CS e no RESTAURAR ORIGINAIS):

| Id | O que faz | Como |
| --- | --- | --- |
| `cs.vmp` | Desliga Plataforma de Máquina Virtual, Hyper-V, Sandbox e WSL **se** não estiverem em uso | `dism /online /Get-FeatureInfo` para ler; `Disable-WindowsOptionalFeature -NoRestart` para cada um que estiver ligado; backup da lista ligada; restaurar religa. Pede reiniciar. Nunca mexe em `HypervisorPlatform` se o VBS estiver rodando sem ele (conferir). |
| `cs.faststartup` | Desliga a Inicialização rápida | `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power\HiberbootEnabled = 0` |
| `cs.dosvc` | Otimização de Entrega só da rede local, sem envio para a internet | Política `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\DODownloadMode = 0` |
| `cs.dnd` | "Não perturbe" automático em jogos e tela cheia | Chaves de `HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings` e regras de foco; conferir os nomes num Windows 11 atual antes de gravar (se não achar, vira orientação) |
| `cs.defender` | Defender: verificação com no máximo 25% de CPU e agendada às 4h | `Set-MpPreference -ScanAvgCPULoadFactor 25 -ScanOnlyIfIdleEnabled $false -ScanScheduleTime 04:00`; backup com `Get-MpPreference` |
| `cs.maintenance` | Manutenção automática às 3h | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance\Activation Boundary` (string de data/hora), backup do valor |
| `cs.edge` | Edge sem inicialização acelerada nem segundo plano | Políticas `HKLM\SOFTWARE\Policies\Microsoft\Edge\StartupBoostEnabled = 0`, `BackgroundModeEnabled = 0` |
| `cs.websearch` | Iniciar sem pesquisa na web | `HKCU\Software\Policies\Microsoft\Windows\Explorer\DisableSearchBoxSuggestions = 1` |
| `cs.storeupdate` | Store sem atualização automática de apps | `HKLM\SOFTWARE\Policies\Microsoft\WindowsStore\AutoDownload = 2` |
| `cs.telemetrylevel` | Telemetria no mínimo permitido | `HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry = 0` (Home trata como 1) |
| `cs.services` (ampliar) | Mais serviços seguros | Acrescentar `WMPNetworkSvc`, `icssvc`, `WpcMonSvc`, `SCardSvr`, `TrkWks`, `AJRouter`, `wisvc`, `SEMgrSvc`, `WalletService` à lista (a trava contra `NeverTouch` continua) |
| `cs.tasks` (ampliar) | Mais tarefas de telemetria | `\Microsoft\Windows\Feedback\Siuf\DmClient`, `DmClientOnScenarioDownload`, `\Microsoft\Windows\Shell\FamilySafetyMonitor`, `FamilySafetyRefreshTask`, `\Microsoft\Windows\Maps\MapsUpdateTask`, `MapsToastTask` |

Fora da lista de ajustes:
- **OneDrive:** cartão "Desinstalar OneDrive" com aviso (arquivos que só estão na nuvem continuam no site); usa o desinstalador do próprio OneDrive.
- **Dispositivos não usados:** cartão que lista áudio, rede e Bluetooth com estado e uso (de `Win32_PnPEntity`), marca os candidatos (adaptador sem cabo, áudio sem nada conectado) e desabilita com `pnputil /disable-device`; restaurar usa `/enable-device`. Nunca mostra GPU, disco, USB, teclado e mouse.
- **Orientações sem gravação** (o app mostra o caminho exato): downloads da Steam durante o jogo, aceleração por GPU da Steam, cache de shaders da NVIDIA "Ilimitado", Reflex On + Boost no CS2, opções de inicialização mínimas.

**Conferir no PC:** APLICAR PC DE CS, reiniciar, conferir Discord, Steam, internet, Synapse e FACEIT; RESTAURAR ORIGINAIS duas vezes e reiniciar.

## Fase 2: MEDIR (passos novos da matriz)

Cada passo diz o que mudar; os que o app consegue gravar viram `Tweak` com restauração, marcados como experimento (fora do APLICAR PC DE CS):

| Passo | Mudança | Gravado pelo app? |
| --- | --- | --- |
| `timer global` | `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\GlobalTimerResolutionRequests = 1`, reiniciar | sim (`exp.timer`) |
| `mpo off` | `HKLM\SOFTWARE\Microsoft\Windows\Dwm\OverlayTestMode = 5`, reiniciar | sim (`exp.mpo`) |
| `rede moderação` | Moderação de interrupções da placa de rede em "Desligado" e depois "Médio" (dois passos) | sim, por `*InterruptModeration` no registro da placa, com backup |
| `sem synapse` | DPI gravado no mouse, Synapse fechado | não (roteiro) |
| `1 monitor` | Segundo monitor desligado durante o jogo | não (roteiro) |
| `preferred cores off` | BIOS: Preferred Cores desligado | não (roteiro de BIOS) |
| `c-states` | BIOS: Global C-State Control desligado e Power Supply Idle Control em "Typical current idle" | não (roteiro de BIOS) |
| `hvci off` | já existe na descoberta do CO | — |

Ordem sugerida na matriz: depois de "bios atualizada" e antes de "co -15", porque são grátis e rápidos de desfazer.

## Fase 3: diagnóstico de versões

- Driver de chipset da AMD: ler a versão instalada do "AMD Chipset Software" (Programas instalados) e o driver de gerenciamento de energia; avisar se tiver mais de um ano, com o link oficial.
- Juntar no cartão "Timer e interrupções" um resumo do que a Fase 1 aplicou e do que falta.

## Fora do plano (documentado como "não fazer")

Opções `-high`/`-threads`, `NetworkThrottlingIndex`, Nagle/TCP, QoS, IPv6/DNS, paginação, compressão de memória, limpadores de RAM contínuos,
Process Lasso, desligar Defender/Store/Windows Update/SVM, mitigações de CPU, Windows modificados. Motivos em `docs/catalogo-otimizacao.md`.

## Riscos

- **Recursos opcionais (VMP/Hyper-V):** se o VBS depender do hipervisor que algum deles liga, desligar pode desligar o VBS e a FACEIT reclama. Conferir
  o estado do VBS (`Win32_DeviceGuard`) depois de reiniciar; o app avisa se o VBS caiu.
- **Desabilitar dispositivos:** um item errado tira som ou rede; por isso lista fechada de classes, confirmação e restauração.
- **Políticas de grupo no Windows Home:** algumas chaves de política são ignoradas; o app lê de volta e diz "sem efeito nesta edição".
