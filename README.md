# AdaptiveNet — rede privada do modpack Deadheim

AdaptiveNet é um mod de rede privado pensado para até 30 jogadores conectados, inclusive duplas e grupos de 8, 10, 12 ou mais pessoas jogando juntas. O servidor continua sendo a autoridade. No modpack Deadheim, o mesmo DLL deve ser distribuído pelo launcher aos clientes para também controlar a fila cliente→servidor; ele não altera o formato dos pacotes nem o mundo salvo.

O objetivo é reduzir atrasos de sincronização quando muitos jogadores estão na mesma área sem deixar jogadores afastados sem atendimento. O mod combina:

- escalonamento justo do envio de ZDOs com limite de tempo de CPU por frame;
- cadência mais rápida para jogadores próximos, detectados dinamicamente;
- controle adaptativo por conexão usando ping, qualidade e fila real do transporte;
- recuo automático quando a conexão mostra congestionamento;
- buffer confiável curto e proporcional à conexão, para não reproduzir combate antigo após uma perda temporária;
- envio Steam com memória fixada no servidor dedicado, evitando uma cópia nativa por pacote;
- telemetria agregada para medir o resultado sem registrar conteúdo dos jogadores;
- caixa-preta por jogador com até 60 segundos antes e 15 segundos depois de uma anomalia;
- relatório cliente→servidor de FPS, frames travados, coletas de GC e fila de upload;
- contagem observacional de ownership somente de personagens e mobs ativos.

O mod é independente e não precisa de nenhum outro mod de rede. Não o execute junto com outro mod que substitua o transporte ou o escalonador de rede; ao detectar um conflito conhecido, AdaptiveNet entra em modo de observação e não muda limites de rede.

## Escopo e requisitos

- Valheim Dedicated Server para Windows, na versão usada para compilar o DLL.
- BepInEx 5.4.23 ou compatível instalado no servidor.
- Backend Steam para receber todas as otimizações de transporte. Com crossplay, recursos genéricos como o escalonador podem continuar úteis, mas os ajustes específicos de `ZSteamSocket` não se aplicam.
- Em uso server-only, clientes vanilla ainda conectam. Para corrigir também a fila de upload dos jogadores neste modpack obrigatório, distribua o mesmo DLL aos clientes.

AdaptiveNet e VBNetTweaks não podem operar juntos. Remova o VBNetTweaks do servidor e do manifesto do launcher antes do teste A/B; apenas desligar algumas opções não remove seus patches carregados.

AdaptiveNet não aumenta sozinho o limite máximo de jogadores do Valheim. O servidor precisa já aceitar 30 conexões por sua configuração ou solução de limite de jogadores. Também não substitui CPU suficiente, boa rota de rede ou banda de upload adequada.

## Instalação recomendada

1. Pare o processo do servidor dedicado.
2. Extraia o pacote privado em uma pasta fora da instalação do Valheim.
3. Abra o PowerShell nessa pasta e execute:

   ```powershell
   .\Install-PrivateServer.ps1 -ServerPath "C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server"
   ```

4. Inicie o servidor e abra `BepInEx\LogOutput.log`.
5. Confirme a presença de linhas contendo `AdaptiveNet 0.3.0 loaded`, `mode=active`, `diagnosticHistory=60s` e a aplicação do patch de orçamento ZDO.

O instalador valida `valheim_server.exe` e `BepInEx\core\BepInEx.dll`, recusa a instalação enquanto houver um processo `valheim_server` ativo e cria backup do DLL anterior. Uma configuração já existente é preservada por padrão. Para substituir deliberadamente a configuração pelo preset deste pacote:

```powershell
.\Install-PrivateServer.ps1 -ServerPath "C:\caminho\do\Valheim dedicated server" -ReplaceConfig
```

Use `-WhatIf` para visualizar as operações sem escrever arquivos.

### Instalação manual

Com o servidor parado, copie:

- `BepInEx\plugins\AdaptiveNet\AdaptiveNet.dll` para a mesma pasta dentro do servidor;
- `BepInEx\config\Detalhes.AdaptiveNet.cfg` para a mesma pasta dentro do servidor.

### Distribuição pelo DeadheimLauncher

Use o pacote `AdaptiveNet-launcher-<versão>.zip`, gerado à parte pelo `New-PrivateServerPackage.ps1`. Ele é plano (`AdaptiveNet.dll` + `config\Detalhes.AdaptiveNet.cfg`, sem a pasta `BepInEx`), no formato que o DeadheimLauncher espera para um mod próprio — não reaproveite o ZIP do servidor, que já vem com a estrutura `BepInEx\...`. Cadastre-o como mod obrigatório do launcher e remova `vbnettweaks` do manifesto. O cliente usa o mesmo `AdaptiveNet.dll`; envio fixado e orçamento agregado continuam exclusivos do dedicated server, enquanto o cliente ganha amostragem da própria rota, fila curta e cadência de upload de 50 ms.

Todos os clientes do Deadheim devem receber exatamente o mesmo DLL 0.3. O relatório diagnóstico é pequeno, versionado e enviado uma vez por segundo diretamente ao servidor. Ele não contém chat, RPCs de gameplay, inventário, endereço IP ou conteúdo dos pacotes.

## Caixa-preta de incidentes

Com `Diagnostics.IncidentTelemetry = true`, o servidor mantém em memória um histórico circular por conexão. Ele abre automaticamente um incidente quando detecta, entre outros sinais:

- fila, qualidade ou inflação de ping ruim naquela conexão;
- relatório do cliente chegando atrasado ou deixando de chegar;
- frame de cliente ou servidor acima do limite configurado;
- coleta de GC ocorrendo no mesmo frame de uma travada.

Os arquivos são gravados no servidor em:

```text
BepInEx/AdaptiveNet/incidents/incidents-AAAAmmdd-HHMMSS-partNN.csv
```

O jogo não é bloqueado para escrever o CSV: um thread em segundo plano possui uma fila limitada. Se o disco não acompanhar, o mod incrementa `dropped_incident_jobs` e preserva o desempenho em vez de esperar pelo arquivo. Por padrão, cada arquivo gira em 16 MiB, são mantidos no máximo 16 arquivos e somente arquivos próprios mais antigos que 7 dias são removidos — teto aproximado de 256 MiB.

### Marcação pelo jogador

Quando perceber teleporte, ações atrasadas ou dano chegando tarde, o jogador deve pressionar **F9** assim que possível. Isso pede ao servidor para preservar o minuto anterior e continuar coletando por mais 15 segundos. Se já houver uma captura ativa, o marcador é anexado ao mesmo incidente.

F8 continua abrindo o overlay. Ele mostra FPS/frame local, quantidade de clientes reportando, maior atraso dos relatórios e o número do incidente ativo.

### Como interpretar

Alguns rótulos importantes na coluna `trigger`:

| Rótulo | Interpretação |
|---|---|
| `network-quality` | A qualidade Steam daquela rota caiu. |
| `network-queue-delay` | A fila de transporte envelheceu. |
| `network-ping-inflation` | O ping subiu enquanto havia demanda. |
| `telemetry-delayed` | O pequeno relatório do cliente ficou preso na fila ordenada; é evidência de atraso de entrega. |
| `client-frame-stall` / `server-frame-stall` | O processo ficou sem produzir frame por tempo excessivo. |
| `client-gc-correlated-stall` / `server-gc-correlated-stall` | Houve coleta de GC no mesmo frame travado; é correlação, não prova isolada de causalidade. |
| `client-report-missing` | O cliente conectado não entregou nenhum relatório compatível no prazo. |

Os campos `server_*` mostram a visão do servidor para aquele jogador. Os campos `client_*` vêm do cliente e mostram a direção de upload dele. Comparar os dois permite separar rota ruim, fila cliente→servidor, travada do cliente e travada global do servidor. `owned_nonplayer_characters` conta somente personagens/mobs ativos atribuídos ao jogador; esta versão não transfere ownership.

Alt-tab, jogador ainda sem personagem e teleporte real são marcados no relatório e não disparam sozinhos uma anomalia de frame. O mod nunca chama `GC.Collect()`.

## Preset para 30 jogadores

O arquivo `config\Detalhes.AdaptiveNet.cfg` do repositório — e `BepInEx\config\Detalhes.AdaptiveNet.cfg` no pacote — usa um perfil conservador para 30 conexões:

| Opção | Valor | Efeito |
|---|---:|---|
| `Scheduler.GroupRadiusMeters` | `160` | Distância usada para reconhecer jogadores juntos. |
| `Scheduler.GroupMinimumPlayers` | `2` | Ativa a cadência rápida quando dois jogadores estão próximos; não limita o tamanho do grupo. |
| `Controller.ServerUploadBudgetMiB` | `12` | Divide até 12 MiB/s de forma justa; grupos recebem peso 1,5. Use `0` somente se houver outro limitador confiável. |
| `Scheduler.GroupedPeerIntervalMs` | `100` | Cadência progressiva: 20 Hz para 2–4, ~13 Hz para 5–8 e 10 Hz para grupos maiores. |
| `Scheduler.SoloPeerIntervalMs` | `250` | Reserva 4 Hz para jogadores espalhados e libera orçamento para o grupo. |
| `Scheduler.MaximumPeersPerFrame` | `12` | Evita uma rajada grande em um único frame. |
| `Scheduler.CpuBudgetMs` | `0.75` | Limite de CPU do escalonador por frame. |
| `ZDO.MaximumQueueBudgetKiB` | `16` | Teto por atendimento para a primeira rodada de medição com carga real. Com `MaximumPeersPerFrame = 12`, um valor de 48 permitia rajadas de até ~576 KiB num único frame do scheduler (57x o vanilla); suba em passos pequenos só depois de confirmar `queue_max_ms` baixo. |

Um grupo de 2, 8, 10, 12 ou mais jogadores é reconhecido automaticamente. O valor `12` em `MaximumPeersPerFrame` é apenas o teto de tentativas em cada frame, não um limite para o grupo.

## Configuração sincronizada e recarga ao vivo

O `Detalhes.AdaptiveNet.cfg` do servidor é o que vale. Pelo ServerSync, cada cliente com o AdaptiveNet recebe os valores do servidor ao conectar e roda com eles, inclusive a cadência de upload e os limiares da caixa-preta, que precisam ser iguais nas duas pontas. Pelo jogo, só admin do servidor altera um valor sincronizado.

Ficam locais, por serem de cada máquina: `Diagnostics.OverlayKey`, `Diagnostics.IncidentMarkerKey`, `Diagnostics.LogIntervalSeconds` e `Diagnostics.CsvTelemetry`.

Cliente vanilla, ou com uma versão anterior ao ServerSync, continua conectando e usa o próprio arquivo. Cliente com AdaptiveNet que já tem ServerSync precisa estar na mesma versão do servidor; por isso, atualize servidor e launcher juntos.

Salvar o `.cfg` com o servidor ligado recarrega a configuração no frame seguinte e a repassa aos clientes conectados, sem reiniciar. As conexões abertas mantêm o que já aprenderam (taxa atual, ping de referência e histórico). A única exceção é desligar o mod ao vivo (`General.Enabled = false` ou `Mode = ObserveOnly`): o orçamento de ZDO e o escalonador voltam ao vanilla na hora, mas os limites Steam já aplicados a uma conexão ficam até aquele jogador reconectar.

## Validação com carga real

Primeiro valide com o preset sem aumentar limites. Durante um evento com 8–12+ jogadores juntos, observe o log a cada 15 segundos. A caixa-preta de incidentes já fica ativa no preset. Para uma comparação agregada A/B, altere temporariamente:

```ini
[Diagnostics]
CsvTelemetry = true
```

O CSV é criado em `BepInEx\AdaptiveNet\telemetry-*.csv`. Depois da coleta, volte `CsvTelemetry = false` para evitar arquivos contínuos.

Indicadores úteis:

- `ping_p95_ms`: latência percebida pelos piores 5% das conexões;
- `queue_max_ms`: maior atraso estimado da fila;
- `congested_peers`: conexões nas quais o controlador recuou;
- `grouped_peers`: jogadores reconhecidos como parte de grupos;
- `scheduler_budget_stops_s`: frames por segundo em que o limite de CPU encerrou o trabalho.
- `local_frame_max_ms`: maior frame do servidor ou cliente que gravou o CSV agregado;
- `missing_client_reports`: clientes conectados sem telemetria compatível;
- `client_report_delay_max_ms`: maior atraso adicional estimado de um relatório cliente→servidor.

Para analisar um relato, guarde o CSV `incidents-*`, o `BepInEx/LogOutput.log` do servidor, o nome do jogador e o horário aproximado. O CSV já contém a linha do tempo dos demais jogadores, permitindo comparar um afetado com outro saudável no mesmo instante.

Se jogadores que estão realmente juntos não forem reconhecidos, aumente `GroupRadiusMeters` em passos pequenos, por exemplo de 160 para 200. Se `scheduler_budget_stops_s` permanecer alto e o servidor ainda tiver folga de CPU, teste `CpuBudgetMs = 1.0`; mude uma opção por vez. Se a fila ultrapassar 180 ms repetidamente ou muitas conexões entrarem em congestionamento, não aumente os limites de envio: verifique a banda de upload, perda de pacotes e uso de CPU do servidor.

## Reversão

Pare o servidor e renomeie `BepInEx\plugins\AdaptiveNet\AdaptiveNet.dll` para `AdaptiveNet.dll.disabled`. Reinicie o servidor; o BepInEx deixará de carregar o mod. O arquivo de configuração e os backups podem permanecer no disco para uma reativação posterior.

## Compilar e gerar o pacote privado

Na raiz do projeto:

```powershell
dotnet build .\AdaptiveNet.csproj -c Release
.\tools\New-PrivateServerPackage.ps1
```

O pacote e seu SHA-256 serão criados em `dist`. O script não instala nem inicia o servidor.

## Instalação

Pelo [DeadheimLauncher](https://github.com/Deadheim-project/Launcher), como mod obrigatório do
servidor e dos clientes. Os releases deste repositório trazem o binário e o preset de
configuração; o launcher baixa de lá pela versão fixada no manifest.
