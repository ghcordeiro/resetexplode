## PC de CS2 (branch `pc-de-cs2`)

Versão pessoal do RESET EXPLODE, só para CS2 neste PC. Mede a estabilidade do fps, aponta o gargalo, deixa o Windows dedicado ao jogo e perdeu tudo que não servia a isso.

**Instalar e atualizar**
- Instale uma vez com o **ResetExplode-Setup.exe** da pre-release mais nova (`cs2-N`) em Releases. Fica em `%LocalAppData%\Programs\ResetExplode`, com atalho no menu Iniciar.
- O app procura versão nova ao abrir e a cada 6 h. Quando houver, aparece **ATUALIZAR** no alto da janela: baixa, confere o SHA-256, troca o arquivo e reabre.
  Não atualiza durante uma gravação.

**Relatar problema** (ícone de inseto no alto da janela)
- Erros inesperados viram relatório automático; o botão manda um relato seu com o laudo da última sessão. Vão como issue no GitHub do app
  (nome de usuário, pasta do perfil e nome do PC são trocados por marcadores). Sem token configurado, ficam guardados e vão depois.
- Na primeira vez, cole um token "fine-grained" do GitHub com acesso só ao repositório `resetexplode` e permissão **Issues: Read and write**.

**Novo nesta versão**
- **TESTE DE RUÍDO** e **repetir N**: a captura se rearma sozinha depois de cada execução; no fim diz se o benchmark varia até 5% (confiável).
- **Matriz guiada**: o passo a passo do PRD dentro do app. USAR O PRÓXIMO PASSO preenche o perfil; com 3 execuções válidas o app decide
  (venceu → vira a linha de base; perdeu ou dentro do ruído → reverta).
- **VALE COMPRAR?**: 5500X3D e 32 GB pelas regras do PRD. Para a RAM, grave um perfil com "uso real" no nome.
- **Paginação**: páginas lidas do disco por segundo em cada sessão; o laudo avisa quando as quedas coincidem com paginação.
- **Aviso de linha de base vencida** quando CS2, driver, Windows ou BIOS mudam depois da medição.
- **Overlays e aceleração** (Otimização): desliga a aceleração de hardware do Discord (com restauração) e mostra onde desligar os overlays de Discord, Steam e NVIDIA.
  Xbox Game Bar entrou nos ajustes do Windows.
- **Modo CS automático** (Otimização): quando o CS2 abre, de qualquer jeito, troca a área de trabalho para 1280x960, fecha o que não está na lista,
  limpa a RAM, para SysMain/Windows Search/Spooler/Windows Update, sobe a prioridade e liga o Alto desempenho; desfaz tudo quando o jogo fecha.
- **Causa de cada queda**: frame a frame pelo PresentMon, cada queda vira CPU, GPU ou "espera do sistema" (driver, interrupção, paginação);
  o laudo mostra a divisão e sugere o diagnóstico profundo quando o sistema é o culpado.
- **Thread principal do CS2** (diagnóstico profundo): quanto de um núcleo a thread mais ocupada do jogo usou em cada segundo (`threads.csv`).
  É a prova direta do limite de CPU que o uso por núcleo esconde.
- A regra do Curve Optimizer passou a usar o clock efetivo de cada núcleo.
- **Matriz**: + PASSOS DE VÍDEO lê o seu `cs2_video.txt` e cria um passo para cada opção que pesa na CPU (o app grava e REVERTER desfaz);
  o passo "ram 3600" pede o roteiro de estabilidade antes (OC Profile, 30 min de stress de RAM, sem WHEA); gráfico do 1% low da linha de base no tempo.
- **Comparar**: separa as execuções por versão do driver da GPU quando há mais de uma; **EXPORTAR CSV** com todas as execuções dos dois perfis.
- **Apps pré-instalados** (Otimização): remove Clipchamp, Notícias, Clima, Xbox, Teams pessoal e afins só para o seu usuário, com reinstalação pela Store.

**Sessões › Benchmark CS2**
- **Perfil de teste** (ex.: "linha de base", "fps_max 240"): cada captura é gravada com o nome do perfil e nunca é apagada pela limpeza de espaço.
- **CAPTURA CS2**: fica armada e começa quando o CS2 está em primeiro plano. Termina no tempo escolhido ou quando o mapa de benchmark imprime
  `[VProf] FPS: Avg=…, P1=…` no `console.log` (abra o jogo com `-condebug`, preset na Biblioteca). Se o 1% low calculado difere mais de 5% do P1 do mapa, avisa.
- Recusa começar com o **FACEIT** aberto (só offline) e marca a execução como inválida se houver alt-tab, mudança de configuração ou poucos quadros.
- **COMPARAR**: mediana e faixa (mín–máx) de cada perfil e veredito: só é "melhor" ou "pior" quando a diferença supera a variação entre execuções repetidas
  da mesma configuração; senão, "dentro do ruído". Mínimo de 3 execuções válidas. **RELATÓRIO HTML** com gráficos, diferença de configuração e sugestões.
- **Latência na comparação**: até a tela, do PC e clique até a imagem (PresentMon), com o mesmo veredito pelo ruído. Decide `fps_max` × G-Sync + Reflex.
- Métricas novas: desvio padrão do frametime, **quedas** (frame acima de 2,5x a mediana do último segundo, sem piso de 33 ms) e regularidade das quedas.
- Snapshot ampliado: BIOS, VBS/Integridade de Memória, Secure Boot, TPM, versão do CS2, vídeo do CS2, `fps_max`, perfil NVIDIA, todos os monitores e programas abertos.
  **PERFIL MANUAL (BIOS)** guarda PBO, Curve Optimizer, timings e tensões, que o Windows não lê.
- **Sugestões** no laudo com a evidência medida: limite de CPU em poucos threads, memória cheia, latência da RAM, quedas periódicas (BIOS/fTPM), fps sem limite,
  clock que cai sem calor (Curve Optimizer), segundo monitor em outra taxa e programa em segundo plano.
- **Alt-tab**: mede quanto o jogo leva para voltar ao ritmo normal. No Launcher, a opção de trocar a área de trabalho do monitor principal para 1280x960 enquanto o
  jogo roda (janela sem borda sem trocar o modo do monitor); o modo original volta ao fechar o jogo ou na próxima abertura do app.
- **Diagnóstico profundo** (DPC/ISR do kernel): aponta o driver que atrasa o sistema. Pesa no jogo; fica fora das comparações.
- **Vídeo do CS2 (teste)**: grava opções de vídeo com backup e restaura. Nunca altera resolução, proporção, taxa nem monitor.

**RESET OTIMIZA › PC de CS**
- **APLICAR PC DE CS**: cria um ponto de restauração e liga os ajustes seguros: serviços que o CS não usa (impressão, telemetria, Xbox, mapas, Bluetooth…),
  placa de rede sem economia de energia, Windows Update com horário ativo máximo e sem reinício com você logado, Widgets/Copilot/dicas desligados,
  efeitos visuais, núcleos da CPU sem estacionamento, tarefas de telemetria e inicialização só com o essencial (Steam, Discord, Razer, áudio, NVIDIA, FACEIT).
  O valor original de cada item é gravado antes da mudança; **RESTAURAR ORIGINAIS** desfaz.
- **PREPARAR PARA JOGAR**: fecha o que não está na lista permitida e limpa a memória em espera.
- Saíram os ajustes que reduziam a segurança (VBS, Spectre/Meltdown, exclusões do Defender): o FACEIT exige VBS, TPM e Secure Boot.
- **Perfil NVIDIA do cs2.exe**: além de limite de fps e baixa latência, modo de energia e V-Sync (só grava depois de o driver confirmar o nome da configuração).

**Removido**: RESET MAKER (Arduino/portas COM/drivers seriais/atraso), Integração (luzes), Instalar apps, FPS estimado, Energia/bateria, benchmarks sintéticos
(render, cena de GPU, ray tracing, simulação, disco), teste de refrigeração e aviso de dispositivos USB. Fica o teste de **Memória** (latência, banda e carregamento).
