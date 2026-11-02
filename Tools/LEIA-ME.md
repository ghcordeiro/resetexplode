# Ferramentas embutidas

Programas de terceiros que vão dentro do RESET EXPLODE e são extraídos na hora de usar.
Antes de cada execução o app confere o SHA-256 (em `EmbeddedTool.cs`) com o arquivo travado contra escrita.

| Ferramenta | Versão | Origem | Licença | Uso |
|---|---|---|---|---|
| PresentMon (`PresentMon/PresentMon-2.6.0-x64.exe`) | 2.6.0 | [GameTechDev/PresentMon](https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0), assinado pela Intel Corporation | MIT (`PresentMon/LICENSE.txt`) | Sessões de jogo: latência até a tela, modo de apresentação, CPU/GPU ocupada por frame |

Para atualizar: troque o arquivo, ajuste o nome e o SHA-256 em `EmbeddedTool.cs` e esta tabela.
