# Coincidência de stalls: "travou junto ou não?"

Ferramenta **offline** (`tools/StallCorrelation/`), sem custo algum em runtime do mod e sem dependência do Valheim.
Lê os CSVs de incidentes (a "caixa-preta") e responde, com estatística, se os stalls dos jogadores e do servidor
acontecem **juntos mais do que o acaso explicaria** (causa compartilhada) ou **independentemente** (causa local).
A técnica (alinhar por segundo e comparar com a hipótese de independência) vem da análise do mod DiagnoseServerLag
(ver `analise-diagnoseserverlag.md`); o código foi escrito do zero, sem copiar nada dele (o mod não tem licença).

## Como usar

```bash
cd tools/StallCorrelation
dotnet build -c Release
dotnet bin/Release/net8.0/StallCorrelation.dll <csv-ou-pasta>... [--limiar 250] [--sem-servidor] [--listar-alone]
```

- Pasta: lê recursivamente todos os `incidents-*.csv`; pode misturar arquivos e pastas.
- `--limiar ms`: frame máximo a partir do qual o segundo conta como stall (padrão 250).
- `--sem-servidor`: tira o servidor da conta, olhando só jogador × jogador.
- `--listar-alone`: lista o horário UTC de cada stall sozinho, por máquina.
- Precisa do .NET 8 (com só o .NET 10 instalado, use `DOTNET_ROLL_FORWARD=Major`).

Testes: `tests/StallCorrelationTests` (também em `tools/Run-Tests.ps1`):

```bash
cd tests/StallCorrelationTests
DOTNET_ROLL_FORWARD=Major dotnet run -c Release
```

## Como funciona

1. **Alinhamento por segundo UTC.** Cada linha do CSV é um jogador em um segundo. O servidor é mais uma "máquina"
   (`server_frame_max_ms`, repetido em todas as linhas do segundo).
2. **Quem entra.** Uma linha de jogador só vale se `client_present`, `client_fresh`, `client_focused`,
   `client_player_ready` e não `client_teleporting` (cliente em segundo plano, carregando ou com relatório velho
   dá frame alto que não é do jogo). As outras linhas são ignoradas: nesse segundo a máquina **não reportou**.
3. **Segundo em stall:** frame máximo ≥ limiar. As máquinas são os **nomes** dos jogadores (o `uid` muda a cada conexão).
   O mesmo (segundo, máquina) que aparece em vários incidentes sobrepostos conta uma vez só.
4. **Taxa de cada máquina:** `p = segundos em stall / segundos em que reportou`.
5. **Esperado por acaso.** Em cada segundo com ≥ 2 máquinas reportando, a chance de ≥ 2 stalls, supondo
   independência com as taxas `p`, é calculada exatamente (Bernoulli independentes). A soma é o esperado.
   O observado é a contagem real de segundos com ≥ 2 stalls. O p-valor vem de Poisson (ou normal, se o esperado é grande).
6. **ALONE.** Stall de uma máquina em segundo em que ao menos outra reportou e **nenhuma** travou. Para cada máquina
   também sai o ALONE esperado (chance de nenhuma outra travar dado que ela travou, pelas taxas das outras).

## Interpretação

| Resultado | Leitura |
|---|---|
| observado ≫ acaso (p < 0,01) | **causa compartilhada** (servidor, rede, host da DatHost) |
| observado ≈ acaso | **coincidência**: a independência já explica |
| observado ≪ acaso | independentes (raro; só aparece com stalls mutuamente exclusivos) |
| ALONE ≪ esperado | os stalls se agrupam: boa parte tem causa comum |
| ALONE ≈ ou > esperado | stalls locais/independentes |

"Dados insuficientes" aparece quando não há excesso e o esperado é < 5 (sem poder para dizer "coincidência").

## Limites estatísticos (ler antes de citar um número)

- **Viés de seleção.** O CSV só existe em volta de incidentes (60 s antes, 15 s depois), e cada incidente é aberto
  por um stall/anomalia. Logo as taxas `p` estão **infladas** e o primeiro stall de cada janela é "garantido".
  O teste continua válido como *comparação interna* (a mesma inflação entra no esperado), mas o **valor absoluto**
  das taxas não vale como taxa de stall do servidor. Para taxa real é preciso a telemetria agregada.
- **Independência entre segundos.** O teste trata cada segundo como independente; stalls duram vários segundos e
  vêm em rajadas, então o p-valor é otimista. A razão observado/esperado e a ordem de grandeza são mais confiáveis que "p < 1e-4".
- **Taxas homogêneas no tempo.** Cada máquina tem uma taxa só. Se vários jogadores travam mais num horário (pico, save),
  isso gera coincidência sem causa direta entre eles (confusão por hora do dia). Ainda é "causa comum", mas não necessariamente o servidor.
- **Resolução de 1 s e atraso do relatório.** O relatório do cliente pode chegar alguns décimos depois; um stall na
  virada do segundo cai em segundos diferentes e vira falso "sozinho". O ALONE é, por isso, um **teto**.
- **Amostras pequenas** (jogadores com poucos segundos) têm taxa ruidosa; olhe o total, não um jogador.
- **Não diz qual é a causa**, só se é compartilhada. Servidor e jogadores juntos apontam para o servidor/host;
  jogadores juntos sem o servidor (`--sem-servidor`) apontam para rede/host de rede ou carga comum.

## Resultado nos dados reais (limiar 250 ms)

Dados: 8 CSVs de produção em `C:\Users\werne\AppData\Local\Temp\deadheim-servidor\` (incidentes de 31/10 a 02/11:
196 849 linhas, 171 092 de jogador válidas, 25 757 ignoradas por foco/pronto/teleporte/relatório velho; 26 127 segundos,
22 jogadores). Os 7 CSVs de `D:\valheimdedicatedserver` (servidor de teste, 934 linhas) são pequenos demais para concluir.

| Pergunta | Observado | Esperado por acaso | Veredito |
|---|---|---|---|
| segundos com ≥ 2 stalls (servidor + jogadores) | 108 | 17,2 (6,3×) | **compartilhado** |
| segundos com servidor + algum jogador juntos | 66 | 10,0 | compartilhado |
| só jogadores (sem servidor) | 91 | 7,3 (12,5×) | **compartilhado** |
| stalls de jogador sozinhos (ALONE) | 297 de 619 (48%) | 597 (96,5%) | agrupam |

Sensibilidade ao limiar: em 150 ms a razão é 3,6× (ALONE 59%), em 500 ms é 19× (ALONE 56%), em 1000 ms são 5 segundos
contra 0,1 esperado. Por arquivo, 6 dos 8 CSVs dão excesso significativo; os outros 2 têm poucos segundos com stall
e não têm poder (um deles, 2 observados contra 2,2 esperados, é compatível com coincidência).

Por jogador, a fração ALONE vai de 4% (Luizkan) a 89% (Detalhes, que tem a maior taxa de cliente, 1,7% dos segundos em stall),
contra ≈ 96% esperado para todos.

### Resposta: "os 63% sozinhos são acaso?"

**Não.** Se os stalls fossem independentes, ~96% deles apareceriam sozinhos (as taxas são de ~0,3–1,7% por segundo).
Aparecem sozinhos 48% (47% sem o servidor): há **muito mais coincidência do que o acaso**, em todos os limiares,
com excesso de ~6× em segundos com ≥ 2 stalls. O "63% sozinho" da contagem por incidente não contradiz isso: lá a unidade
é o incidente, aberto pelo próprio stall do jogador, e o critério é "ninguém mais no incidente", que não é o mesmo
que "ninguém mais travou naquele segundo". Leitura prática: **uma parte relevante dos stalls é compartilhada**
(jogador e servidor juntos 66 vezes contra 10 esperadas, e jogadores entre si sem o servidor 91 contra 7) e **a outra
parte, cerca de metade, é local** (cliente, sobretudo Detalhes e dan; e o servidor: 84% dos stalls do servidor
acontecem sem nenhum jogador travar, p. ex. save de mundo que o cliente não sentiu em 250 ms).

Com os limites acima (viés de seleção e dependência entre segundos), o "6×" é uma ordem de grandeza, e o ALONE real pode
ser um pouco menor que 48% por causa do atraso de relatório. A conclusão "não é acaso" é robusta; o número exato, não.
