# AdaptiveNet

Mod de rede do modpack **Deadheim**, distribuído pelo [DeadheimLauncher](https://github.com/Deadheim-project/Launcher).

Ajusta a sincronização de rede do servidor dedicado e coleta telemetria de diagnóstico
por conexão, sem alterar o formato dos pacotes nem o mundo salvo.

- Escalonamento de ZDO por proximidade, no lugar do rodízio de um peer por frame do vanilla.
- Orçamento de fila e limites de transporte adaptativos por conexão.
- Caixa-preta de incidentes: janela de 60 s antes e 15 s depois de uma anomalia.

Não pode rodar junto com outro mod que substitua o transporte ou o escalonador de rede
(VBNetTweaks, BetterNetworking, SkadiNet, LeanNet e similares). Ao detectar um desses,
o AdaptiveNet entra em modo de observação e não altera nenhum limite.

## Instalação

Pelo DeadheimLauncher, como mod obrigatório do servidor. Os releases deste repositório
contêm apenas o binário e o preset de configuração — o código-fonte não é publicado aqui.
