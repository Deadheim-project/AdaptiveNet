# Análise do DiagnoseServerLag (DeathMonger) — o que dá para aproveitar

Fonte lida (somente leitura): https://github.com/gbahns/ValheimMods, pasta `DiagnoseServerLag/`
(commit `96543b8`, 24 arquivos .cs, ~7,5 mil linhas; o fonte declara 0.10.9 em `DiagnoseServerLagMod.ModVersion`,
a página do hexium cita 0.10.21). Nada foi executado nem copiado; abaixo só técnicas, citando classes/métodos.

## 1. Licença (verificada)
- **Não há arquivo LICENSE/COPYING** na raiz nem em `DiagnoseServerLag/`; `README.md`, `manifest.json`, `thunderstore.toml`,
  `hexium.toml` e `Directory.Build.props` não mencionam licença (busca por licen*, MIT, Apache, SPDX, copyright: nada).
- Sem licença explícita = todos os direitos reservados por padrão. **Não reutilizar código.** Reimplementar a *ideia/técnica*
  (APIs públicas do próprio Valheim) é seguro; copiar trechos não. Se quisermos algo literal, pedir permissão ao autor.

## 2. Arquitetura em resumo
- **Um único plugin BepInEx** (`DiagnoseServerLagMod`) que roda igual no cliente e no servidor dedicado (sem `[BepInProcess]`).
  Instalado nos dois lados: o servidor responde, o cliente pergunta e monta o veredito.
- `Sampler.Tick()` (chamado do `Update` do plugin) fecha **uma amostra por segundo** (`Sampler.Commit`) numa ring buffer
  (`Ring` de structs `Sample`, ~110 B cada, 1 h por padrão). Jogo pausado congela a coleta (`Game.IsPaused`).
- `LagNetwork`: RPCs via `ZRoutedRpc` com `ZPackage` (`DSL_Request/Report`, `DSL_Capture*`, `DSL_ClientSeries`, `DSL_Echo*`).
  O cliente pede o relatório a cada N s; o servidor responde `ServerReport.FromLocal().Pack()` (layout versionado por byte).
  Servidor sem o mod não responde → após 3 silêncios o cliente marca `ServerModule.Absent`.
- `Verdict.Diagnose()`: regras independentes (`AddServerStarved`, `AddLinkSaturated`, `AddConnectionQuality`, `AddObjectChurn`,
  `AddSceneLoading`, `AddGarbageCollection`, `AddThisMachine`); cada uma devolve um `Finding` com confiança 0-100 e evidências; ordena por confiança.
- UI (`LagPanel`, `LagHud`, `UiKit`), config (`DslConfig`) e comandos `dsl_*` (`Commands`): não nos interessam.

## 3. Tabela métrica → técnica
| Métrica | Onde | Técnica / API |
|---|---|---|
| Tick do servidor | `Sampler.Tick/Commit`, `ServerReport.FromLocal` | **Tempo de frame do processo** (`Time.unscaledDeltaTime`) agregado por segundo: média, máx e nº de stalls (frame ≥ `StallMs`, padrão 100 ms). Mediana em 60 s = `BaselineTickMs`. Reconhece servidor com frame cap (`ServerPaced`: tick máx ≈ mediana → não é lentidão). |
| Bytes na fila do socket | `Sampler.CollectSockets` | `ZNetPeer.m_socket.GetSendQueueSize()` por peer no servidor (`GetConnectedPeers()`, só `IsReady()`); no cliente, o socket de `GetServerPeer()`. `Mathf.Max(0, …)` porque o jogo devolve negativos. `GetCurrentSendRate()` lido à parte. Cada leitura em try próprio (PlayFab lança `NotImplementedException`). Limiares 16 KiB (aviso) / 64 KiB (grave) + inclinação (`Stats.Slope`) para "enchendo". |
| Perda/qualidade e ping | `Sampler.Commit`, `CollectSockets`; `Verdict.AddConnectionQuality` | `ZNet.GetNetStats(out localQ, out remoteQ, out ping, out outBps, out inBps)` (cliente; no servidor é a média dos peers) e `ISocket.GetConnectionQuality(...)` por peer. Só `ZSteamSocket` preenche; ping 0 e qualidade 0 = "não medível" (`HasPing`). Jitter = variação média do ping segundo a segundo (`Stats.Jitter`). Alertas: qualidade < 0,98 / < 0,90. **Não chama ISteamNetworkingSockets direto**: só o que o ZNet já expõe. |
| Ping alternativo | `LagNetwork.Ask/RPC_Report`, `PeerRoundTripMs` | Round trip do próprio RPC `DSL_Request` (seq + eco), suavizado (2:1), quando o socket não informa ping. |
| ZDOs / churn | `Sampler.Commit`; `Verdict.AddObjectChurn` | `ZDOMan.NrOfObjects()`, `GetSentZDOs()`, `GetRecvZDOs()`, `GetClientChangeQueue()` (contadores que o próprio `ZDOMan` atualiza 1×/s: custo ~0). Churn = mediana de `ZdosRecv` na janela (10 s) ≥ 3× a mediana da baseline (60 s) e ≥ piso 150/s. |
| Carregamento de mundo | `Sampler.CountOwnedObjects`, `AddSceneLoading` | `ZNetScene.NrOfInstances()` e delta de instâncias por segundo (`Loading` se ≥ limiar); a regra só dispara com stall + subida de instâncias. |
| Cadência de feed | `Feed.cs` | Harmony **postfix em `ZDOMan.RPC_ZDOData`** no cliente: mediana (64 amostras) do intervalo entre pacotes de ZDO (`Feed.IntervalMs`); previsão `50 + peers*frame` (a mesma fórmula do round robin vanilla que já mapeamos). |
| GC / CPU / memória do processo | `Machine.Fill` | `Process.TotalProcessorTime` (ms de CPU/s), `GC.CollectionCount(0/1/2)` por segundo, `GC.GetTotalMemory(false)`, `Process.WorkingSet64`. Regra de GC: % dos segundos com stall que tiveram coleta vs % dos segundos quietos. |
| CPU/RAM da máquina | `SystemCpu`, `SystemMemory` | `GetSystemTimes` (P/Invoke, Windows) ou `/proc/stat` (Linux) → separa "o jogo travou sozinho" de "outro processo roubou CPU". |
| Dono dos monstros / "simulating" | `Sampler.CountOwnedAI`, `CountOwnedObjects`, `DescribeOtherOwners` | Ver seção 4. |
| Latência por dono | `LagNetwork.TrackOwners/SendEchoes` | Para os até 4 donos com mais objetos perto, manda RPC `DSL_Echo` **direto ao uid do dono** (roteado pelo servidor) e mede a volta (`RPC_EchoReply`); só responde com `ShareMyPerformance`. Mede "quão lento é mexer no que fulano simula". |
| Capturas em grupo (`dsl_bench`) | `LagNetwork.AskCapture/RPC_Capture/BeginGather/FinishGather`, `ClientSeries`, `GroupReport.Build` | Admin cliente → `DSL_Capture` → servidor confere admin (`ZNet.IsAdmin(peer.m_socket.GetHostName())`) → `DSL_CaptureAll` a todos os peers → cada cliente devolve `DSL_ClientSeries` (janela de N s de `Sample` + hardware) comprimida (`WriteCompressed`) → timeout 8 s → servidor monta texto + CSV e devolve (limite 256 KiB comprimido). |
| Correlação de stalls | `GroupReport.Correlate` | Alinha as séries por **segundo UTC** (`Sample.UtcTicks`); usa só os segundos que todas as máquinas reportaram; calcula quantos segundos teriam ≥2 stalls *por acaso* dada a taxa de cada máquina e compara com o observado (juntos > acaso → compartilhado/servidor; ≈ acaso → coincidência; < acaso → independentes). Lista "ALONE" por máquina. O servidor entra como mais uma máquina. |
| Integração com outro mod | `YieldInfo` | Reflexão (`AccessTools.TypeByName("SpreadTheLoad.SpreadTheLoadApi")`) para saber quem está sendo poupado. Padrão útil para ler nosso scheduler sem dependência dura. |

## 4. "simulating" e a telemetria de ownership zerada (ponto central)
- **Como o DSL calcula**: `Sampler.CountOwnedAI` itera `BaseAI.Instances` (lista estática de IAs *instanciadas nesta máquina*),
  pega o `ZNetView` e compara `view.IsOwner()`; o dono dos demais vem de `zdo.GetOwner()` e é contado por uid
  (nome via `ZNet.GetPlayerList()`, `m_characterID.UserID`). `CountOwnedObjects` faz o mesmo sobre `ZNetScene.m_instances`
  (campo privado, lido com `AccessTools.Field`; dicionário ZDO→ZNetView), com cache por prefab para "é peça construída".
- **Onde roda de verdade**: o "simulating X of Y" do relatório é o **do cliente** (`ClientSeries.OwnedAI/NearbyAI` vêm da
  última amostra de cada cliente, enviada em `DSL_ClientSeries`; `GroupReport` agrega "quem simula a zona").
  Ou seja, **o DSL não descobre o dono no servidor dedicado**: cada cliente conta o que *ele* instanciou.
  O mesmo código existe no servidor, mas o autor comenta que lá só se instancia o que cai ao redor da origem
  (`ZNet.m_referencePosition` fica em zero): 82 instâncias contra 430 mil ZDOs numa medição dele.
- **Conclusão**: a técnica do DSL **não corrige** o nosso 0. Nossa `CharacterOwnershipTelemetry` usa `Character.GetAllCharacters()`
  no servidor, limitação equivalente (objetos não instanciados). Copiar o `BaseAI.Instances` no servidor também daria 0.
- **Caminhos que funcionam para nós** (só o 1 é a ideia do DSL):
  1. **Lado do cliente**: nossos clientes já mandam telemetria (`client_telemetry_peers`); acrescentar `BaseAI.Instances` + `IsOwner`
     por segundo (dezenas de IAs, custo desprezível) e agregar no servidor por uid. Dá "quantos monstros cada jogador simula, de quantos que vê". Só vale para quem tem o mod.
  2. **Lado do servidor, por ZDO** (o que a nossa memória já sugeria): percorrer só os setores ao redor de cada peer (`m_refPos`)
     no índice de setores do `ZDOMan` e, para ZDOs cujo prefab está num conjunto de criaturas (hash resolvido uma vez por prefab via
     `ZNetScene.GetPrefab(hash)` + `GetComponent<Character>/BaseAI`, cacheado como o `IsPiece` do DSL), contar `zdo.GetOwner()`.
     Sem instanciar nada e sem varrer os ~860 mil ZDOs. Validar nomes dos campos privados contra o assembly original.
  3. Combinar 1+2: o servidor confere o que cada cliente diz que simula.

## 5. Aproveitável no AdaptiveNet
- **Veredito por causa** com confiança e evidências (`Finding`): hoje a caixa-preta abre incidente por limiares; um rótulo de causa
  (servidor / enlace / qualidade / churn / carregamento / GC / máquina local) facilita ler os CSVs.
- **Correlação de stalls contra o acaso** (`Correlate`): rigorosa e sem custo de runtime; responde "stall de cliente é local ou compartilhado?"
  (hoje: 294 incidentes, 63% com o jogador sozinho) com significância em vez de contagem. Precisa de timestamp UTC por segundo de cada cliente.
- **Qualidade de conexão por peer** (`GetConnectionQuality`, `GetNetStats`): perda de pacote e jitter parecem não estar no nosso CSV; duas colunas por peer são baratas e separam ping alto real de perda.
- **Baseline por mediana para churn** (`ZdosRecv` 10 s vs 60 s ×3): `GetRecvZDOs/GetSentZDOs/GetClientChangeQueue` são contadores gratuitos.
- **Eco por dono** (`DSL_Echo` ao uid do dono): mede o atraso real até o dono do mob, respondendo a pergunta de 29/10 (dono com FPS ruim prejudicando os outros) mesmo sem ownership no servidor.
- **Captura em grupo sincronizada**: servidor pede a janela de N s a todos e junta por UTC — formato para um "dump de incidente" que inclui os clientes.
- **Tick sem confundir frame cap** (`ServerPaced`): evita falso "servidor lento" no nosso ~30 FPS de batchmode.
- **GC correlacionado**: comparar % de GC em segundos com stall vs quietos é novo para nós (já temos `local_gc_collections`).
- **CPU da máquina do servidor** (`/proc/stat`): distingue host oversubscribed (DatHost) de problema do processo.

## 6. Riscos
- **Licença**: sem LICENSE → não copiar código; reimplementar do zero a partir das APIs do jogo.
- **Custo por frame**: o DSL é leve (contadores, 1 amostra/s). Exceções: `CountOwnedObjects` percorre todas as instâncias do `ZNetScene` 1×/s no cliente (milhares → sub-ms); `Process.Refresh()`/`GC.GetTotalMemory` 1×/s. Para nós, qualquer varredura de ZDO no servidor tem de ser por setor e amortizada (`SendZDOs` já custa ~1 ms com 5+ peers e o orçamento de CPU é apertado).
- **Compatibilidade com o scheduler**: o DSL **não faz patch no envio** (só postfix em `RPC_ZDOData` e patches de UI/`ZNet.Shutdown`). Não colide com nosso patch de `SendZDOs`/`CreateSyncList`. Cuidados: nomes de RPC (`DSL_*` vs nossos) e não ter dois mods mexendo nos mesmos contadores do `ZDOMan`.
- **Deadheim**: instalar o DSL no servidor exige Whitelist do AzuAntiCheat (regra fixa de deploy) e RPCs extras nos clientes; preferível portar a ideia para o nosso mod a instalar mais um.
- **Honestidade das métricas**: ping/quality de PlayFab/TCP são zeros; no Deadheim (Steam) funciona, mas tratar 0 como "não medido" (`HasPing`).
- **Rede**: relatórios RPC pequenos (~1 KB a cada 5-30 s); captura em grupo até 256 KiB comprimidos — evitar em horário de pico.

## 7. Ideias priorizadas
1. **(Alta, barata)** Corrigir ownership: `OwnedAI/NearbyAI` por cliente na telemetria existente (4.1) e, depois, contagem por ZDO/setor no servidor (4.2). Desbloqueia a análise de "dono com FPS ruim".
2. **(Alta)** Colunas de qualidade de conexão por peer (`GetConnectionQuality` local/remoto + jitter do ping) no CSV e no incidente — separa os 225-250 ms reais de perda de pacote.
3. **(Alta)** Teste de coincidência de stalls contra o acaso, primeiro offline sobre os CSVs que já temos (zero custo de runtime).
4. **(Média)** Rótulo de causa por incidente (servidor/enlace/qualidade/churn/carga/GC/cliente) reaproveitando limiares nossos; sem UI.
5. **(Média)** Eco RPC direto ao uid dos donos de mobs (máx. 4 a cada 4 s) para medir latência "até o dono".
6. **(Média)** Churn = `ZdosRecv/ZdosSent` vs mediana de 60 s, para marcar picos de objetos (raid, base grande entrando).
7. **(Baixa)** CPU total do host via `/proc/stat`, se a DatHost permitir.
8. **(Baixa)** Captura de grupo sob demanda (comando admin) devolvendo a janela de N s de todos os clientes num CSV.
9. **(Descartar)** Painel/HUD, pausa do jogo, GraphicsConfig e dicas de hardware: fora do escopo do servidor PvP.
