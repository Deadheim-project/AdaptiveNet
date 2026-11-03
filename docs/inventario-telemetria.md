# AdaptiveNet — inventário de telemetria e lacunas

Levantamento só de leitura, em 2026-11-03, sobre `src/`, o `.cfg` e o README. Os números de linha são da árvore de trabalho daquele dia. Ela tinha alterações não commitadas, entre elas a agregação do log (`AccumulateHealthForLog`) e `DistantPlayerSyncPatch.cs`. O que vale para o HEAD (0.5.4) e o que só existe na árvore vem marcado.

Referência de comparação: o mod DiagnoseServerLag (DeathMonger, v0.10.21) classifica o lag em 6 causas: **tick do servidor, fila do socket (bytes), perda de pacotes e jitter, churn de objetos vs baseline, carregamento de mundo, máquina local**. Ele também faz capturas em grupo e correlaciona stalls coincidentes entre jogadores.

## 1. Como a coleta funciona

- **Amostra a cada 1 s.** `NetworkRuntime.Tick` ([NetworkRuntime.cs:399](../src/NetworkRuntime.cs)) roda a cada `Controller.SampleIntervalSeconds` (1 s, mínimo 0,25 s). Em cada passo ele:
  - lê a conexão de cada peer (`TrySample`, [NetworkRuntime.cs:955](../src/NetworkRuntime.cs));
  - fecha a janela de frames (`RuntimeHealthMonitor.TakeInterval`);
  - fecha a janela PvP (`PvpTelemetry.TakeWindow`);
  - monta um `PeerTelemetryPoint` por jogador e o põe em `ConnectionContext.History`, um anel de 60 s (`Diagnostics.HistorySeconds`).
- **Transporte Steam.** `SteamTransport.TrySample` ([SteamTransport.cs:9](../src/SteamTransport.cs)) chama `GetConnectionRealTimeStatus`. Se a conexão não for Steam ou a chamada falhar, `ConnectionSampler` cai nas métricas vanilla do `ISocket`. Nesse caso só existem ping, qualidade, fila e taxa.
- **Cliente.** Cada cliente com o mod faz a mesma amostragem da própria rota (cliente→servidor) e envia um relatório por RPC a cada 1 s, protocolo 2 ([ClientTelemetryRpc.cs](../src/ClientTelemetryRpc.cs)). O servidor guarda o último e estima o atraso de entrega (`DeliveryDelayEstimator`).
- **Saídas.** São três:
  - o log periódico, a cada 15 s;
  - o overlay, que não tem tecla por padrão desde a 0.5.4 e abre pelo menu ESC > Deadheim > Admin;
  - os CSVs: o agregado, desligado por padrão, e o de incidentes (a "caixa-preta").
- **Caixa-preta.** O CSV de incidentes (`IncidentTelemetry`) tem 1 linha por jogador por segundo e só é gravado em volta de um incidente: 60 s antes e 15 s depois. Cada incidente despeja o histórico de **todos** os jogadores, não só do afetado. Isso já é uma captura em grupo.

## 2. Métricas que já existem

"Log" é a linha `telemetry ...` de 15 s, ou as linhas de evento (`peer-link`, `relay hold`, `World save held...`). "CSV-ag" é o CSV agregado. "CSV-inc" é o CSV de incidentes.

| Métrica | Onde é coletada | Frequência | Onde aparece |
|---|---|---|---|
| Ping (`m_nPing`) por peer | `SteamTransport.TrySample` | 1 s | log (`ping avg/p95`, `peer-link`), overlay, CSV-ag (`ping_avg_ms`, `ping_p95_ms`), CSV-inc (`server_ping_ms`, `client_ping_ms`) |
| Qualidade da conexão local/remota (0–1) | idem (`m_flConnectionQualityLocal/Remote`) | 1 s | log só dentro de `peer-link`, CSV-inc (`server_quality_*`, `client_quality_*`) |
| Fila em bytes: gerenciada (Valheim), pendente confiável/não confiável, enviada-não-confirmada | idem + `GameAccess.GetSendQueue` | 1 s | log (`queued` = soma de todos os peers, em KiB), overlay, CSV-ag (`queued_bytes`), CSV-inc (`*_managed_queue_bytes`, `*_pending_*`, `*_unacked_bytes`) |
| Atraso de fila (`m_usecQueueTime`) | idem | 1 s | log (`queueMax`, máximo do intervalo), overlay, CSV-ag (`queue_max_ms`), CSV-inc (`*_queue_delay_ms`) |
| Taxa de envio Steam real, B/s e pacotes/s entrada e saída | idem | 1 s | log (`sendRate avg/min`), overlay, CSV-ag, CSV-inc (`*_transport_rate_bps`, `*_out_bps`, `*_in_bps`, `*_packets_s`) |
| Decisão do controlador AIMD: congestionado, motivo, teto de taxa, cota de ZDO | `AdaptiveConnectionController.Observe` | 1 s | log (`congested`, `rateCapAvg`, `zdoAvg`, linhas `peer-link degraded/recovered`), CSV-inc (`congested`, `decision_reason`, `adaptive_rate_limit_bps`, `zdo_budget_bytes`) |
| Saúde de frames do **servidor**: média, p95 (por balde), máx, frames ≥250 ms e ≥500 ms, coletas de GC 0/1/2, "frame com GC", memória gerenciada | `RuntimeHealthMonitor.RecordFrame/TakeInterval`, por frame | janela de 1 s | log (`localFps`, `localFrameMax`; **na árvore** agora cobrem os 15 s inteiros, no HEAD só o último segundo), CSV-ag, CSV-inc (`server_frame_*`, `server_gc*`) |
| Saída do `ZNet.SaveWorld` na thread principal (`worldSave`) | `PvpTelemetry` (hook em `SaveWorld`) | por save | log (linha própria a cada save e `worldSave=` na linha de 15 s), CSV-ag (`world_save_ms`), CSV-inc (`server_world_save_ms`), rótulo `server-world-save` |
| Saúde de frames do **cliente** (mesmos campos acima) | relatório RPC | 1 s | log (`clientFrameMax`), overlay, CSV-ag (`client_frame_max_ms`), CSV-inc (`client_frame_*`, `client_gc*`, `client_managed_memory_bytes`) |
| Contexto do cliente: com foco, jogador pronto, teleportando | `RecordFrame`/`ResetClientContextWindow`, no relatório | 1 s | CSV-inc (`client_focused`, `client_player_ready`, `client_teleporting`) |
| Atraso de entrega do relatório e idade do relatório | `DeliveryDelayEstimator` / `RemoteClientTelemetryState` | 1 s | log (`reportDelayMax`), overlay, CSV-ag, CSV-inc (`client_delivery_delay_ms`, `client_report_age_seconds`) |
| Clientes reportando e clientes sem relatório | `RemoteClientTelemetryState` | 1 s | log (`clientReports`, `missing`), overlay, CSV-ag |
| `relayHold`: atraso de posição entre jogadores dentro de `GroupRadiusMeters` | `PvpTelemetry`/`PvpMetrics.RelayHoldTracker` | contínua, janela de 1 s | log (`relayHold n/p95/max` e aviso `relay hold` acima de 2 s), overlay, CSV-ag, CSV-inc (`relay_hold_*`) |
| `hitWait`: espera de golpe na fila da vítima | `PvpTelemetry`, patches em `ZRoutedRpc` | por golpe | log, overlay, CSV-ag, CSV-inc (`hit_forward_*`) |
| `hitUpload`: espera de golpe no atacante | cliente → relatório | por golpe | log (`hitUploadMax`), overlay do cliente, CSV-inc (`client_hit_upload_*`) |
| `serviceGapMax`: maior intervalo entre envios de ZDO a um jogador | `PvpTelemetry` | contínua | log, CSV-ag, CSV-inc (`zdo_service_gap_max_ms`, `zdo_service_calls`) |
| `zdoRefused`: recusas pela guarda de fila (`SendZDOs`) | `PvpTelemetry` | por tentativa | log, CSV-ag, CSV-inc (`zdo_queue_refusals`) |
| Escalonador: tentativas/s, paradas por orçamento de CPU/s, jogadores agrupados e vizinhos por peer | `AdaptivePeerScheduler` | 1 s | log (`sched`, `budgetStops`, `grouped`), overlay, CSV-ag, CSV-inc (`nearby_players`) |
| Mobs ativos e ownership | `CharacterOwnershipTelemetry.Refresh` | 5 s | log (`mobs=`), CSV-inc (`*_nonplayer_*`, `ownership_*`). **Sempre 0 no servidor dedicado**, ver memória `adaptivenet-ownership-telemetry-zero` |
| Backend online (`Steamworks`/crossplay) | `CheckOnlineBackendOnce` | 1 vez | log, CSV-ag |
| Incidente ativo e jobs descartados do writer | `ProcessIncidents` | 1 s | log (`incident=`), overlay, CSV-ag |

Quatro observações sobre o que a tabela mostra:

- **Qualidade só é visível no log quando há congestionamento.** Ela aparece dentro das linhas `peer-link`. A linha de 15 s não traz qualidade nem perda.
- **O que a janela de 1 s esconde.** No HEAD, a linha de log mostra só o último segundo de cada 15 s (corrigido na árvore, sem commit). O CSV-inc tem 1 s de resolução e só existe em volta de incidentes.
- **Aviso do `README.md`.** O texto diz "inflação de ping, fila e qualidade ruins" como gatilhos. Na prática eles passam todos pelo bit único `Decision.Congested`.
- **`TotalQueuedBytes` inclui bytes não confirmados.** `unacked` entra na soma. Com ping alto (225–250 ms) isso infla a "fila" e causa recusas de ZDO, mesmo sem enfileiramento real (memória `adaptivenet-zdo-refusals-high-ping`).

## 3. Como o veredito de incidente é decidido hoje

Não existe um "veredito". Existe uma lista de **gatilhos** (strings concatenadas) que abre a captura. A causa fica a cargo de quem lê o CSV.

### 3.1 Gatilhos por jogador (`ClassifyPeerAnomaly`, [NetworkRuntime.cs:707](../src/NetworkRuntime.cs); `DiagnosticAnomalyClassifier.ClassifyPeer`, [DiagnosticModels.cs:218](../src/DiagnosticModels.cs))

Tudo é avaliado a cada amostra de 1 s, **depois** de uma carência de `max(5 s, 3 × intervalo do relatório)` desde a conexão.

| Rótulo | Regra (padrão do preset) |
|---|---|
| `network-quality` | `min(qualidade local, remota) < 0,88` (só se as duas ou uma forem conhecidas, 0–1) |
| `network-queue-delay` | `m_usecQueueTime ≥ 180 ms` |
| `network-ping-inflation` | houve demanda (fila+não confirmado ≥ 4 KiB, ou saída ≥ 35% do teto) **e** `ping ≥ baseline + 45 ms`. O baseline é uma média móvel (EWMA) que só sobe devagar quando a fila e a qualidade estão calmas. |
| `telemetry-delayed` | atraso de entrega do relatório ≥ 500 ms, **apenas** com o cliente em jogo (pronto, sem teleporte) **e** com foco |
| `client-gc-correlated-stall` | frame com GC ≥ 250 ms; só vale com foco e em jogo |
| `client-frame-stall` | frame máximo do cliente ≥ 500 ms; só vale com foco e em jogo |
| `client-report-missing` | nunca recebeu relatório e o personagem existe há ≥ max(4 s, 2,5 × intervalo). Dispara uma vez por queda (latch). |
| `transport-sample-unavailable` | o link não pôde ser amostrado depois da carência. Uma vez por queda. |
| `peer-disconnected` | o jogador saiu depois de ter estado congestionado ou ter tido anomalia nos últimos `max(20 s, cooldown/2)` |

### 3.2 Gatilhos do servidor (`ClassifyServer`, [DiagnosticModels.cs:264](../src/DiagnosticModels.cs))

Ordem de prioridade. Só o primeiro que casar sai.

1. `server-world-save`: save ≥ 250 ms.
2. `server-gc-correlated-stall`: frame com GC ≥ 250 ms.
3. `server-frame-stall`: frame máximo do servidor ≥ 500 ms. O limiar é o `SevereFrameStallThresholdMs`: um frame de 250–499 ms sem GC nem save **não** abre incidente.

### 3.3 Manual

- Marcador do jogador: no máximo 1 por jogador a cada 30 s.
- Capturas manuais: no máximo 1 por minuto em todo o servidor.
- Um marcador fora do limite ainda é anexado, no `trigger`, a qualquer captura aberta naquele momento.

### 3.4 Abertura e fechamento

- **Cooldown.** `ProcessIncidents` ([NetworkRuntime.cs:747](../src/NetworkRuntime.cs)) abre uma captura se houver qualquer gatilho e `agora ≥ _nextAutomaticIncidentTime`, que é o início do último incidente automático + 45 s.
- **Captura.** Ela grava o histórico de todos os jogadores (fase `pre`), depois o segundo seguinte de cada jogador durante 15 s (fase `post`).
- **Durante a captura.** Gatilhos novos só são anexados ao texto do `trigger`, limitado a ~768 caracteres.
- **Formato do `trigger`.** `player=<nome>;uid=<id>;reason=a+b | player=...`, mais o rótulo do servidor.
- **Onde a causa fica registrada.** No log (`diagnostic incident #N started: <trigger>`) e na coluna `trigger` do CSV-inc. O texto só repete o gatilho e não resume nem dá a causa em linguagem simples.

### 3.5 Falhas conhecidas do veredito atual

- **Congestionado vira um bit.** Fila, qualidade e inflação de ping viram `network-*`, sem separar "link saturado" de "perda".
- **Falta um rótulo para o servidor lento sem pico.** Um servidor a 20 FPS sustentados nunca dispara, só os picos.
- **Não há correlação entre jogadores.** Se 8 jogadores travam juntos, saem 8 gatilhos de peer em vez de "foi o servidor".
- **A carência não é rótulo.** Carregamento e teleporte são suprimidos (silenciados), não explicados.

## 4. Lacunas frente às 6 causas do DiagnoseServerLag

| Causa | Situação | O que já existe | O que falta | Ponto natural no código |
|---|---|---|---|---|
| **1. Tick do servidor sobrecarregado** | **Coberta** (para picos), **parcial** (para carga sustentada) | Frames do servidor: média, p95, máx, stalls, GC e save. Gatilhos `server-frame-stall`, `server-gc-correlated-stall`, `server-world-save`. Escalonador: `budgetStops/s` e `sched/s`. | Servidor lento *sem* pico (ex.: média de frame ≥ 1,5× o alvo, ~50 ms em batchmode a ~30 FPS, durante vários segundos). Nenhum rótulo para isso e o limiar de 500 ms deixa passar 250–499 ms sem GC. Sem separação entre "SendZDOs custou X ms" e o resto do frame. | `DiagnosticAnomalyClassifier.ClassifyServer` (novo rótulo `server-overloaded` por média móvel de N s, com histórico do servidor em `NetworkRuntime`). Custo de ZDO por frame: `AdaptivePeerScheduler` já mede o orçamento de CPU. |
| **2. Link saturado (bytes na fila do socket)** | **Parcial** | Todos os bytes (gerenciada, pendente confiável/não confiável, não confirmada), atraso de fila, taxa real e vazão de entrada/saída por peer, no servidor e no cliente. | Gatilho em *bytes*; hoje só vale o atraso ≥ 180 ms. Falta o rótulo "saturação": saída ≈ taxa Steam (150 KiB/s) por N s, ou bytes pendentes crescendo. `unacked` infla a fila com ping alto, então a métrica precisa separar `pending` de `unacked`. | `AdaptiveConnectionController.Observe` (já tem `hasDemand` e `queueCongested`) para separar `queue-delay` de um novo `link-saturated` (`out_bps ≥ 0,9 × transport_rate` com pendente > X). Os campos já estão em `NetworkSample`. |
| **3. Qualidade da conexão (perda de pacotes e jitter)** | **Parcial** | Qualidade local/remota do Steam (`m_flConnectionQualityLocal/Remote`) é uma proxy de perda: rótulo `network-quality` com limiar 0,88. Ping instantâneo e pacotes/s. | **Perda em % não existe**; `1 − qualidade` é a aproximação disponível na API usada. **Jitter não existe**: só há um ping por segundo e nenhuma variância. A qualidade não aparece na linha de 15 s. | Jitter e perda podem ser calculados **no servidor**, sem mudar o protocolo, a partir do `History` do peer: desvio ou média das diferenças de `server_ping_ms` na janela e `1 − min(quality)`. Local: ao montar o `PeerTelemetryPoint` em `Tick`, ou numa função pura em `DiagnosticModels.cs` (testável). |
| **4. Churn de objetos vs baseline** | **Ausente** | Nada mede criação/destruição de ZDOs ou objetos. A telemetria de ownership/mobs é **sempre 0** no dedicado (memória `adaptivenet-ownership-telemetry-zero`). `PvpMetrics` só detecta ZDO recriado para o `relayHold`. | Contagem de ZDOs (total e por zona/peer), taxa de criação/destruição por segundo, e uma mediana móvel para comparar ("churn vs mediana recente"). | Contagem barata por amostra a partir do `ZDOMan` (nº de objetos, via `GameAccess`), mais um contador de ZDOs enviados por peer dentro do patch de `SendZDOs` (`ZdoBudgetPatch.cs`/`PvpProbePatches.cs`). Mediana em `RuntimeHealthMonitor` ou num novo tipo em `DiagnosticModels.cs`. **Não** varrer todos os ZDOs na thread principal (~860 mil, já descartado). |
| **5. Carregamento de mundo** | **Parcial** | O servidor sabe quando o personagem do cliente passa a existir (`CharacterSince`) e há carência de conexão. O cliente reporta `PlayerReady` e `Teleporting`. O save de mundo do servidor tem nome próprio. | Carregamento/teleporte hoje são **suprimidos** (sem gatilho), não explicados: o incidente some em vez de dizer "cliente carregando". Falta também "jogador ainda sem personagem" como estado visível, e o tempo de carga de zonas no servidor. | `ClassifyPeer`: em vez de `return result` quando `!inGame`, emitir um rótulo informativo (`client-loading`, `client-teleporting`) que não abre captura mas entra no veredito. Dados já em `RemoteClientTelemetrySnapshot`. |
| **6. Máquina local (cliente)** | **Parcial** | FPS/frames do cliente (média, p95, máx), stalls, GC e memória gerenciada; gatilhos `client-frame-stall` e `client-gc-correlated-stall`; foco da janela. | Sem CPU/GPU/RAM do sistema (só memória gerenciada do Mono), nem separação "GPU-bound vs CPU-bound". Alt-tab é suprimido, não explicado. | Cliente: `RuntimeHealthMonitor` e `ClientTelemetryRpc` (protocolo versionado: campos novos exigem subir o protocolo e manter servidor e launcher na mesma versão). Para um primeiro passo, `SystemInfo`/`Time.deltaTime` não exigem mudar o RPC se o veredito usar só o que já chega. |
| *Extra: capturas em grupo* | **Coberta** | Cada incidente despeja o histórico de todos os jogadores, e o `trigger` lista quem disparou. | — | `IncidentTelemetry.QueueHistories` |
| *Extra: correlação de stalls coincidentes* | **Ausente** | Só há análise manual a posteriori no CSV (a conclusão "ver quem travou junto" depende de quem lê). | Contar, a cada segundo, quantos peers estão em stall ou congestionados ao mesmo tempo. Se ≥ 2 e o servidor também está em stall, a causa é o servidor. | `NetworkRuntime.Tick`, logo antes de `ProcessIncidents`: o laço de peers já tem a lista de anomalias do segundo. |

## 5. Caberia um "veredito por causa" em linguagem simples?

Sim, e é uma mudança pequena e de baixo risco, porque quase tudo já está em `PeerTelemetryPoint`/`History`:

- **Onde.** Uma função pura nova em `DiagnosticModels.cs`, ao lado de `DiagnosticAnomalyClassifier` e testável fora do jogo (como as suítes em `tests/`). Entrada: o histórico (pre+post) dos peers, a saúde do servidor e quem disparou. Saída: lista de causas, cada uma com severidade e uma frase, por exemplo:
  - `servidor lento` — "o servidor travou 640 ms; todos os 8 jogadores sentiram junto";
  - `link saturado` — "Fulano está enviando 148 KiB/s, o limite da conexão";
  - `conexão ruim` — "Fulano tem qualidade 0,71 e ping variando ±60 ms";
  - `carga de objetos` — "o mundo criou 4× mais objetos que a mediana recente";
  - `carregando mundo` — "Beltrano ainda carrega a zona";
  - `máquina do jogador` — "o PC de Ciclano travou 1,2 s, sem queda de rede".
- **Quando executar.** Na abertura do incidente (`ProcessIncidents`, quando já se tem o `pre`) e no fechamento (`_activeIncidentId = 0`, quando a janela `post` está completa). Só o fechamento enxerga o incidente inteiro.
- **Onde registrar.** Uma linha de log `diagnostic incident #N verdict: ...` e uma coluna no fim do CSV-inc (colunas só se acrescentam ao final, como já foi feito nas colunas de PvP e `server_world_save_ms`).
- **Sem protocolo novo.** Perda, jitter, servidor lento, saturação e correlação saem do que o servidor já tem. Só o caso "máquina local" detalhado (CPU/GPU) e o carregamento de zona no cliente pediriam campos novos no relatório (protocolo 3).
- **Cuidado de interpretação.** Nenhum desses vereditos é prova de causa. São regras sobre correlação, como o próprio README já diz de `*-gc-correlated-stall`. O texto deve dizer "indício de".
- **Ordem sugerida.**
  1. Correlação entre peers + `server-overloaded`.
  2. Jitter e perda a partir do `History`.
  3. `link-saturated` separando `unacked` de `pending`.
  4. Rótulos de carregamento e teleporte.
  5. Churn de ZDOs.
  6. Detalhe da máquina do cliente (protocolo 3).

## 6. Limitações deste levantamento

- Não foram lidos linha a linha `PvpTelemetry.cs`, `PvpMetrics.cs`, `ClientTelemetryRpc.cs` (parte de estado) e `AdaptivePeerScheduler.cs`. As informações vêm dos comentários, dos nomes de campos e do uso em `NetworkRuntime.cs`.
- Nada foi executado nem medido; frequências e limiares são os do código e do `.cfg` do repositório. O `.cfg` de produção na DatHost pode diferir.
- A leitura incluiu alterações não commitadas do usuário na árvore (`NetworkRuntime.cs`, `Settings.cs`, `DistantPlayerSyncPatch.cs`). Elas não foram tocadas.
