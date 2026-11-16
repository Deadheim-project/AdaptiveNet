# Handoff: disconnects/rollback de Kabritin e Mafalda (2026-11-15)

## Relato
- Mafalda: levou disconnect e rollback ontem (14/11). Kabritin comecou a ter o mesmo hoje (15/11).
- Logs de cliente analisados (em C:\Users\werne\Downloads):
  - `Deadheim-LogOutput-20261115-115357.log` = Kabritin (persona "Kabrito Lixerooo"), fuso BRT (servidor = UTC; 11:45 dele = 14:45 UTC).
  - `Deadheim-LogOutput-20261115-164434.log` = Mafalda, mesmo relogio do servidor.
- Log do servidor: `deadheim-deploy/bin/servidor.ps1 log "<regex>"` (baixa para `%TEMP%\deadheim-servidor\LogOutput.log`; cobre so desde o ultimo restart, 07:01 de 15/11). Nao cobre ontem.

## Achado principal
O mod **ServerCharacters 1.4.17** (cliente) desconecta o proprio cliente:

    [Server Characters] Disconnecting -187589249. Compressed data sending for event
    'ServerCharacters PlayerInventory' timed out after 30 seconds.

O servidor registra `ClosedByPeer` no mesmo segundo. O limite de 30 s e do mod (nao ha chave na `org.bepinex.plugins.servercharacters.cfg` do servidor).

| Quem | Kick (servidor, UTC) | Contexto |
|---|---|---|
| Kabritin | 14:32:19 (log do cliente dessa sessao nao disponivel) | logo apos incidente #61: save do mundo 917 ms; eco de dono dele 1767 ms |
| Kabritin | 14:45:20 | reentrou 14:42; `client-loading` 11 s; servidor gerou dungeon Crypt3 as 14:43:46; incidente #62/#63 |
| Mafalda | 16:34:04 | entrou em dungeon 16:32:07 (Spawning dungeon 16:32:13); save do mundo 1049 ms as 16:31:53 (incidente #77) |
| Mafalda | 15:15:47 | outro mecanismo: `ZRpc timeout` do servidor (30 s sem pacotes); reportDelayMax=14807 ms; incidente #66 teleporte/GC no cliente |

- Rede estava boa em todos (Kabritin ping 29-56 ms; Mafalda ping fixo 228-266 ms, estavel). Servidor 30 FPS, sem congestao.
- Travada de login no cliente: Kabritin frame de 14.543 ms, Mafalda 16.187 ms (= sincronizacao inicial do ServerCharacters).
- Hipoteses (nao confirmadas): dungeon (2 casos, so hipotese), envio do inventario preso por stall do cliente, ACK do servidor atrasado. Rollback e consistente: se o cliente cai antes do inventario chegar, o servidor fica com o perfil antigo. Falta saber o que a Mafalda perdeu e quando.

## Bug confirmado no AdaptiveNet (nao explica os kicks)
`queueMax=9223372036854780ms` (overflow ~long.MaxValue) na telemetria de cliente (login dos dois) e no servidor (`peer-link degraded ... queue=9223372036854780ms`, incidente #63).
Origem provavel: `m_usecQueueTime` em `src/SteamTransport.cs:41` e `:89` sem tratar valor sentinela/negativo da Steam. Efeito: `queueCongested` em `src/AdaptiveController.cs` pode cortar o rate cap sem motivo. Corrigir com teste.

## Versoes
- Servidor e clientes rodando AdaptiveNet **0.6.0**. Repo (branch main, limpo) esta em 0.6.1 (nao implantado).
- Restart de producao e deploy so com confirmacao do usuario. Nao abrir o Valheim sem OK (memoria `no-launching-valheim`).

## Proximos passos sugeridos
1. Olhar o log do servidor entre 16:32 e 16:34 (e 14:43-14:45) para ver o que segurou o ACK do inventario (dungeon, dono de mobs, ZDO churn).
2. Corrigir o overflow da fila no `SteamTransport.cs` (+ teste).
3. Pedir o log do cliente do Kabritin da sessao anterior (14:32 UTC) e da Mafalda de ontem; perguntar o que ela perdeu no rollback.
4. Testar entrada em dungeon com 2 clientes para reproduzir (so com OK do usuario).

## Cuidado
`org.bepinex.plugins.servercharacters.cfg` do servidor tem uma URL de webhook do Discord em texto puro: nao colar em chat/relatorio.

## Analise (2026-11-15, continuacao)

### Causa: orcamento de ZDO do AdaptiveNet acima do limite do ServerCharacters
ServerCharacters 1.4.17 (decompilado, `Shared.sendCompressedDataToPeer`): antes de cada fragmento espera
`while (peer.m_socket.GetSendQueueSize() > 20000)`; se a fila nao cair abaixo de 20000 bytes em nenhum frame por
30 s, desconecta o proprio peer. O mesmo padrao existe no ServerSync embutido (`ConfigSync.cs`). O inventario e
enviado a cada `Inventory.Changed` (minerar, pegar item, craftar), entao qualquer jogador fazendo coisas dispara.

`GetSendQueueSize` (vanilla) = fila gerenciada + pending + **unacked**. O guard do `SendZDOs` deixa essa fila encher
ate o orcamento do AdaptiveNet. Vanilla = 10 KiB (< 20000, nunca trava o ServerCharacters). O `[ZDO]
MaximumQueueBudgetKiB` (sincronizado, vale tambem para o upload do cliente) subiu de 16 para 32 KiB no restart de
14/11 07:02 UTC (CSV de incidentes: `zdo_budget_bytes` max 16384 ate 06:45, 32768 desde 07:03). Primeiro kick: Mafalda em 14/11.

Evidencia nos logs dos clientes (telemetria do proprio cliente, 15 s cada):
- Kabritin, 2 min antes do kick 14:45:20: `queued` 14-32 KiB, `zdoAvg=32,0KiB`, `grouped=1`, FPS 75-82, frameMax 28-156 ms, ping 56 ms.
  Cliente fluido e rede boa: a fila ficou acima de 20000 por ser o teto do guard (dono de meteoros do evento Fimbulvinter, Lox, mineracao).
- Mafalda, 2 min antes do kick 16:34:04: `queued` 23-32 KiB em todas as linhas, FPS 61-80, ping 228 ms.
  Com 228 ms de RTT, so o unacked de ~90 KiB/s ja passa de 20 KB: por isso ela e a mais afetada.

Descartado: dungeon. "Loading/Spawning dungeon" aparece dezenas de vezes nos dois logs sem kick (e o carregamento de
cripta perto do jogador); o servidor gerou ~10 criptas entre 16:19 e 16:21 sem kick. Stall do cliente tambem nao (FPS normal).

Rollback: o inventario nunca chega ao servidor, entao o perfil salvo no servidor fica o anterior. Consistente.

O caso 15:15:47 da Mafalda (`ZRpc timeout` no servidor, reportDelayMax 14,8 s, teleporte/GC) e outro mecanismo e nao
e explicado por isto.

### Consequencia para o 0.6.1 (NAO implantar como esta)
0.6.1 soma o unacked (ate 64 KiB) ao orcamento: a fila pode ficar em ate 96 KiB, nos dois sentidos. Isso trava o
ServerCharacters no cliente (inventario/perfil) e no servidor (envio de PlayerProfile) e o ServerSync (configs), para
qualquer jogador com upload moderado, nao so ping alto.

### Correcao proposta
1. Imediato (config, precisa OK + restart/reload): `[ZDO] MaximumQueueBudgetKiB = 16` no servidor (volta ao valor
   sem kicks ate 13/11). Volta o teto de vazao da Mafalda (~58 KiB/s, memoria zdo-refusals-high-ping), mas sem kick.
2. Codigo (0.6.2): o guard precisa deixar `GetSendQueueSize` cair abaixo de 20000 regularmente. Proposta: "janela de
   drenagem": se a fila ficou >= ~19 KiB por N s (ex. 5 s), recusar ZDO ate ela cair abaixo; so entao liberar o
   orcamento alto de novo. Custa ~1 RTT de pausa a cada N s e mantem o ganho do 0.6.1. Teste unitario do estado.
   Alternativa mais simples: limitar o orcamento efetivo (com unacked) a 19 KiB sempre que houver envio
   ServerCharacters/ServerSync pendente (exige detectar o coroutine; mais fragil).
3. Overflow `queueMax=9223372036854780ms`: `SteamTransport.cs:39-41` e `:89` fazem `Math.Max(0, usec/1000)`, que nao
   filtra o sentinela INT64_MAX da Steam. Tratar `< 0` ou acima de um teto (ex. 60 s) como desconhecido (0) + teste.
