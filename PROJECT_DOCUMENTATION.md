# Documentação Completa do Projeto — SubtitleTranslator

> **Estado deste documento:** o inventário detalhado abaixo contém descrições de funcionalidades planejadas que não correspondem ao código atual. Para o estado verificado por arquivo, limitações e resultados de testes, consulte [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md). Em caso de divergência, o código e `ARCHITECTURE_SPEC.md` são a referência; não presuma que itens desta documentação histórica já existem.

Este documento descreve de forma detalhada tudo o que o projeto **SubtitleTranslator** possui em sua implementação atual: arquitetura, modelo de domínio, persistência, motor de legendas SRT, provedores de IA, pipeline de execução, serviços em background, interface web (Razor Pages), segurança, observabilidade, implantação e suíte de testes.

---

## 1. Visão Geral

O **SubtitleTranslator** é um orquestrador configurável para tradução automática de legendas `.srt` (Inglês $\rightarrow$ Português do Brasil `pt-BR`), construído em **.NET 10** com **ASP.NET Core Razor Pages**, **Entity Framework Core** e **SQLite**, projetado para rodar continuamente como serviço Linux (`systemd`) em ambientes Proxmox/servidor local.

### Principais Capacidades
- Monitoramento contínuo de diretórios de mídia e criação automática de jobs de tradução.
- Preservação estrita de numeração e timestamps originais do arquivo SRT (o modelo LLM traduz apenas texto indexado em JSON).
- Suporte a múltiplos provedores configuráveis via interface web:
  - **Ollama** (execução local)
  - **OpenRouter** (roteamento multi-modelo em nuvem)
  - **OpenAI-Compatible** (endpoints compatíveis com `/v1/chat/completions`)
  - **Azure OpenAI** (execução síncrona por deployment)
  - **Azure OpenAI Batch** (execução assíncrona em lote via Files API + Batches API JSONL com até 50% de desconto)
- **Pipelines com Fallback Inteligente**: re-chunking por etapa (step), retentativa seletiva apenas dos blocos ainda não traduzidos e escrita atômica do arquivo final (`.tmp` $\rightarrow$ `.pt-BR.srt`).
- **Versionamento de Prompts e Glossário** com suporte a **Azure Prompt Caching** (`prompt_cache_key`, `cached_tokens` e `cache_write_tokens`).
- **Recuperação Automática após Reinício**: retomada de jobs interrompidos e reconciliação de batches Azure em andamento.
- **Observabilidade e Segurança**: Health checks (`/health`, `/health/live`, `/health/ready`), métricas Prometheus (`/metrics`), autenticação por token para operadores com rate limiting e proteção de segredos via variáveis de ambiente.

---

## 2. Estrutura de Diretórios e Projetos

```text
SubtitleTranslator.slnx
├── ARCHITECTURE_SPEC.md                # Especificação arquitetural de referência
├── GITHUB_COPILOT_MASTER_PROMPT.md     # Guia mestre de fases de implementação
├── PROJECT_DOCUMENTATION.md            # Inventário completo do que o projeto possui
├── README.md                           # Guia rápido de uso e execução
├── deploy/
│   ├── monitoring/
│   │   └── subtitletranslator-alerts.yml # Regras de alerta Prometheus
│   └── systemd/
│       └── subtitletranslator.service    # Unit file hardened para Linux/Proxmox
├── docs/
│   └── deployment/
│       └── systemd.md                    # Guia de implantação em produção Linux
├── src/
│   └── SubtitleTranslator/
│       ├── Application/
│       │   ├── Contracts/              # Contratos de tradução, batch e modelos
│       │   ├── Interfaces/             # Abstrações de providers e credenciais
│       │   ├── Options/                # Opções de Scanner, Worker, DashboardAuth, MetricsAuth
│       │   └── Services/               # Serviços de aplicação, pipeline, recovery, workers e queries
│       ├── Domain/
│       │   ├── Entities/               # Entidades persistidas no SQLite via EF Core
│       │   ├── Enums/                  # Enumerações de status e estratégias
│       │   └── ValueObjects/           # Objeto de valor Subtitle
│       ├── Infrastructure/
│       │   ├── Configuration/          # EnvironmentCredentialResolver
│       │   ├── Metrics/                # PrometheusMetricsFormatter
│       │   ├── Persistence/            # AppDbContext e Migrations SQLite
│       │   ├── Providers/              # Implementações Ollama, OpenRouter, AzureOpenAi, AzureBatch
│       │   └── Security/               # DashboardAuthenticationDefaults e LoginAttemptRateLimiter
│       ├── Pages/                      # Interface Web e Administração (ASP.NET Core Razor Pages)
│       ├── Srt/                        # Parser, Writer, Validator, Chunker e TokenEstimator
│       └── Program.cs                  # Bootstrap, DI, Middlewares, Health Checks e Endpoints
└── tests/
    ├── SubtitleTranslator.UnitTests/        # 45 testes unitários
    └── SubtitleTranslator.IntegrationTests/ # 22 testes de integração
```

---

## 3. Modelo de Domínio e Persistência (`Domain` & `Infrastructure/Persistence`)

O banco de dados utiliza **SQLite** gerenciado pelo **Entity Framework Core** (`AppDbContext`) com migrations versionadas.

### 3.1 Entidades (`Domain/Entities`)
1. **`ProviderDefinition`**:
   - Representa uma conexão com provedor de IA (`Name`, `ProviderType`, `Endpoint`, `AuthenticationType`, `CredentialReference`, `ApiVersion`, `Enabled`, `Priority`, `TimeoutSeconds`, `ConfigurationJson`).
   - Possui campos de saúde monitorados em background (`HealthStatus`, `HealthMessage`, `LastHealthCheckAt`, `ConsecutiveFailures`).
2. **`ModelDefinition`**:
   - Define um modelo vinculado a um `ProviderDefinition` (`Name`, `ModelIdentifier`, `DeploymentName`, `Enabled`, `Priority`, `SupportsBatch`, `SupportsStructuredOutput`).
   - Parâmetros de geração e chunking (`ContextWindow`, `MaxOutputTokens`, `Temperature`, `TopP`, `ChunkStrategy`, `MaxChunkSize`, `PromptProfile`).
   - Tarifação por 1 milhão de tokens (`InputCostPer1M`, `OutputCostPer1M`, `CachedInputCostPer1M`) e configurações avançadas em `ConfigurationJson` (`batchDiscountPercent`, `maxParallelRequests`, `pipelineMaxAttempts`, `pipelineRetryDelayMilliseconds`).
3. **`TranslationPipeline` e `PipelineStep`**:
   - `TranslationPipeline`: define uma sequência nomeada de execução (`Name`, `Description`, `IsDefault`, `Enabled`).
   - `PipelineStep`: define cada passo ordenado por `Order` (`ModelDefinitionId`, `ExecutionMode` — `Synchronous` ou `AzureBatch`, `Enabled`, `RetryLimit`, `TimeoutSeconds`, `FallbackBehavior`).
4. **`PromptTemplate` e `PromptVersion`**:
   - `PromptTemplate`: agrupador lógico de prompts por par de idiomas (`Name`, `Description`, `SourceLanguage`, `TargetLanguage`, `Enabled`).
   - `PromptVersion`: versão imutável ou editável de instruções (`PromptTemplateId`, `Version`, `Name`, `SystemPrompt`, `UserPromptTemplate`, `GlossaryJson`, `IsActive`, `SupportsPromptCache`, `PromptCacheKey`).
5. **`OrchestratorSettings`**:
   - Configuração global singleton (`Id = 1`) que armazena `DefaultPipelineId`, `DefaultPromptVersionId` e `ScannerPaused`.
6. **`TranslationJob`**:
   - Representa a tradução de um arquivo `.srt` (`SourceFilePath`, `OutputFilePath`, `PipelineId`, `PromptVersionId`, `Status`, `TotalSubtitles`, `TranslatedSubtitles`, `TotalInputTokens`, `TotalOutputTokens`, `TotalCachedTokens`, `EstimatedCostUsd`, `LastError`, `CreatedAt`, `StartedAt`, `CompletedAt`).
7. **`SubtitleChunk`**:
   - Bloco de legendas processado dentro de um job (`TranslationJobId`, `ChunkIndex`, `StartSubtitleIndex`, `EndSubtitleIndex`, `Status`, `SourcePayloadJson`, `TranslatedPayloadJson`, `AssignedProviderId`, `AssignedModelId`, `BatchJobId`, `AttemptCount`, `InputTokens`, `OutputTokens`, `CachedTokens`, `CacheWriteTokens`, `EstimatedCostUsd`, `LatencyMs`, `LastError`).
8. **`ChunkAttempt`**:
   - Histórico auditável de cada tentativa de tradução de um chunk (`SubtitleChunkId`, `AttemptNumber`, `ProviderId`, `ModelId`, `ExecutionMode`, `Status`, `RequestId`, `BatchId`, `BatchCustomId`, `InputTokens`, `OutputTokens`, `CachedTokens`, `CacheWriteTokens`, `EstimatedCostUsd`, `LatencyMs`, `HttpStatusCode`, `ErrorCode`, `ErrorMessage`, `StartedAt`, `CompletedAt`).
9. **`AzureBatchJob`**:
   - Estado completo de um lote submetido ao Azure OpenAI Batch (`TranslationJobId`, `ProviderId`, `ModelId`, `ExternalBatchId`, `InputFileId`, `OutputFileId`, `ErrorFileId`, `Status`, `CompletionWindow`, `TotalRequests`, `CompletedRequests`, `FailedRequests`, `CreatedAt`, `SubmittedAt`, `InProgressAt`, `FinalizingAt`, `CompletedAt`, `ExpiresAt`, `CancelledAt`, `LastPolledAt`, `LastError`).
10. **`WorkerRuntimeState`**:
    - Estado operacional do processo em background (`Id = 1`, `WorkerInstanceId`, `StartedAt`, `LastHeartbeatAt`, `LastRecoveryAt`, `LastScanAt`, `ActiveJobId`, `ActiveStage`, `ActiveStatusMessage`).

### 3.2 Enumerações (`Domain/Enums`)
- `ProviderType`: `Ollama`, `OpenRouter`, `AzureOpenAi`, `OpenAiCompatible`
- `AuthenticationType`: `None`, `ApiKey`, `BearerToken`, `AzureApiKey`, `ManagedIdentity`
- `ChunkStrategy`: `MaxLines`, `MaxCharacters`, `MaxTokens`
- `ExecutionMode`: `Synchronous`, `AzureBatch`
- `FallbackBehavior`: `NextStep`, `RetryThenNextStep`, `FailJob`
- `JobStatus`: `Pending`, `Queued`, `Running`, `WaitingBatch`, `Validating`, `Completed`, `Failed`, `Cancelled`
- `ChunkStatus`: `Pending`, `InProgress`, `BatchSubmitted`, `Completed`, `Failed`, `ValidationFailed`
- `ProviderHealthStatus`: `Unknown`, `Healthy`, `Degraded`, `Unavailable`, `Disabled`

---

## 4. Motor de Processamento SRT (`Srt/`)

1. **`SrtParser`**:
   - Faz o parsing de arquivos `.srt` suportando quebras de linha `\r\n` e `\n`, múltiplas linhas de texto por legenda e preserva índices e timestamps (`Start`, `End`).
2. **`SrtWriter`**:
   - Reconstrói o arquivo `.srt` no formato padrão (`00:00:00,000 --> 00:00:00,000`) usando exclusivamente os timestamps originais.
   - Realiza escrita atômica (`WriteFileAtomicallyAsync`): grava em arquivo temporário `.tmp` no mesmo diretório e executa `File.Move(..., overwrite: true)` somente após validação completa.
3. **`SrtValidator`**:
   - Valida a tradução antes de gerar o arquivo final:
     - Quantidade exata de blocos igual ao original;
     - Índices idênticos e na mesma ordem;
     - Timestamps `Start` e `End` inalterados;
     - Ausência de textos vazios quando o original possuía conteúdo.
4. **`SubtitleChunker` e `TokenEstimator`**:
   - Divide as legendas de acordo com a política do modelo de cada step:
     - `MaxLines`: limite de itens por chunk;
     - `MaxCharacters`: limite de caracteres somados por chunk;
     - `MaxTokens`: estimativa conservadora de tokens (`TokenEstimator`) considerando overhead do envelope JSON.

---

## 5. Provedores de Tradução (`Infrastructure/Providers/`)

Todos os provedores implementam contratos padronizados (`ITranslationProvider` para chamadas síncronas e `IBatchTranslationProvider` para submissão assíncrona em lote), resolvendo segredos apenas em memória via `EnvironmentCredentialResolver` (`ICredentialResolver`).

1. **`OllamaProvider` (`Infrastructure/Providers/Ollama/`)**:
   - Integração com `/api/chat` local, suporte a schema estruturado JSON de saída (`format`), `temperature`, `top_p`, `num_predict` e parse resiliente da resposta.
2. **`OpenRouterProvider` (`Infrastructure/Providers/OpenRouter/`)**:
   - Integração com `/chat/completions` compatível tanto com `ProviderType.OpenRouter` quanto com `ProviderType.OpenAiCompatible`.
   - Suporte a autenticação Bearer, API Key ou sem autenticação (para servidores locais compatíveis com OpenAI).
   - Controle de concorrência por provedor via `SemaphoreSlim` (`maxParallelRequests`).
   - Extração de métricas de uso (`prompt_tokens`, `completion_tokens`, `cached_tokens`, `cache_write_tokens`).
3. **`AzureOpenAiProvider` (`Infrastructure/Providers/AzureOpenAi/`)**:
   - Integração síncrona com deployments Azure OpenAI (`/openai/deployments/{deployment}/chat/completions?api-version=...`).
   - Suporte a retentativas automáticas em erros transitórios (`408`, `429`, `5xx`).
   - Suporte a **Azure Prompt Caching**: envio de `prompt_cache_key` quando `SupportsPromptCache` está ativo e leitura de `cached_tokens` / `cache_write_tokens`.
4. **`AzureBatchProvider` (`Infrastructure/Providers/AzureBatch/`)**:
   - Implementação completa do ciclo Azure OpenAI Batch API (`/openai/v1/files` e `/openai/v1/batches`):
     - Geração de arquivo JSONL em UTF-8 sem BOM com `custom_id` exclusivo por chunk (`job-{jobId}-step-{stepId}-chunk-{chunkIndex}-attempt-{n}`);
     - Inclusão de `prompt_cache_key` nas linhas JSONL quando habilitado na versão do prompt;
     - Upload do arquivo (`purpose = "batch"`), criação do batch (`completion_window = "24h"`), polling de status (`GetStatusAsync`), download de resultados e arquivos de erro (`GetResultsAsync`) e cancelamento (`CancelBatchAsync`).

---

## 6. Orquestração, Pipeline, Custos e Serviços em Background (`Application/Services/`)

1. **`TranslationPipelineExecutor`**:
   - Executa os steps do pipeline vinculado ao job em ordem de prioridade (`Order`).
   - Realiza **re-chunking dinâmico**: aplica a estratégia e tamanho de chunk específicos do modelo de cada step apenas sobre as legendas que ainda não foram traduzidas.
   - Em steps **Síncronos**: suporta paralelismo configurável (`maxParallelRequests`), retentativas por chunk (`RetryLimit` / `pipelineMaxAttempts`) e registra cada tentativa em `ChunkAttempt`.
   - Em steps **Azure Batch**: submete o lote, persiste `AzureBatchJob`, aguarda conclusão via polling com persistência de progresso, processa linhas de saída/erro e encaminha apenas os chunks com falha para o próximo step de fallback (`FallbackBehavior`).
   - Ao final, valida todas as legendas com `SrtValidator` e escreve o arquivo `.pt-BR.srt` de forma atômica.
2. **`TranslationCostCalculator`**:
   - Calcula o custo estimado em USD por chunk considerando:
     - Tokens de entrada não-cacheados (`InputTokens - CachedTokens`) $\times$ `InputCostPer1M`;
     - Tokens de entrada em cache (`CachedTokens`) $\times$ `CachedInputCostPer1M`;
     - Tokens de saída (`OutputTokens`) $\times$ `OutputCostPer1M`;
     - Desconto percentual configurável para execuções em lote (`batchDiscountPercent`, ex.: 50% no Azure Batch).
3. **`TranslationRecoveryService`**:
   - Executado na inicialização do worker para garantir tolerância a falhas e reinícios:
     - Reconcilia jobs em `WaitingBatch` consultando o estado atual do batch no Azure;
     - Reprocessa outputs de batches já concluídos enquanto a aplicação estava offline;
     - Reencaminha jobs interrompidos (`Running` / `Validating`) para retomada segura aproveitando chunks já traduzidos no SQLite.
4. **`TranslationScannerService`**:
   - Varre periodicamente os diretórios configurados em `Scanner:Directories`, ignora arquivos já traduzidos (`.pt-BR.srt`, `.por.srt`) ou já existentes no banco, e enfileira novos `TranslationJob` fixando o pipeline e a versão de prompt padrão vigentes.
   - Pode ser pausado/retomado em tempo real pela interface web.
5. **`TranslationWorkerService` e `WorkerStatusService`**:
   - Serviço hospedado (`BackgroundService`) que executa a recuperação inicial, atualiza o heartbeat em `WorkerRuntimeState`, dispara ciclos do scanner e processa a fila de jobs pendentes.
6. **`ProviderHealthCheckService` e `ProviderHealthMonitorService`**:
   - Sonda periodicamente os provedores habilitados (`/api/tags` no Ollama validando também se os modelos configurados estão instalados; `/models` ou endpoint base em OpenRouter/Azure) e atualiza `HealthStatus`, `HealthMessage` e `ConsecutiveFailures`.

---

## 7. Interface Web e Administração (`Pages/`)

Construída em **ASP.NET Core Razor Pages** com layout responsivo Bootstrap:

1. **Dashboard Home (`/Index`)**:
   - Cartões de resumo operacional: contadores de jobs (Pending, Running, WaitingBatch, Completed, Failed), tokens totais (Input, Output, Cached), custo estimado acumulado em USD, status do Worker/Heartbeat, controle de pausa do Scanner (`Pause` / `Resume`) e tabela de saúde dos Providers e Batches ativos.
2. **Jobs (`/Jobs/Index` e `/Jobs/Details`)**:
   - Listagem filtrável por `Status` com progresso de legendas, pipeline/prompt utilizados, duração calculada (`Duration`), tokens e custo estimado.
   - Página de detalhes (`/Jobs/Details/{id}`) com linha do tempo de eventos, lotes Azure Batch, estado de cada chunk e histórico completo de tentativas (`ChunkAttempts` com `InputTokens`, `OutputTokens`, `CachedTokens`, `CacheWriteTokens`, latência, HTTP status e mensagens de erro).
3. **Providers (`/Providers/Index`, `Create`, `Edit`)**:
   - Cadastro e edição de provedores, listagem de modelos vinculados a cada provedor, status de saúde em tempo real e botão **Test Health** individual ou global.
4. **Models (`/Models/Index`, `Create`, `Edit`)**:
   - Configuração completa de modelos, vínculo com provider, estratégia e tamanho de chunk, parâmetros de inferência (`Temperature`, `TopP`, `MaxOutputTokens`, `ContextWindow`), flags (`SupportsBatch`, `SupportsStructuredOutput`), prioridade e tarifas de custo por 1M de tokens (`InputCostPer1M`, `OutputCostPer1M`, `CachedInputCostPer1M`).
5. **Pipelines e Steps (`/Pipelines/Index`, `Create`, `Edit` e `/Pipelines/Steps/Index`, `Create`, `Edit`)**:
   - Criação de pipelines, seleção do pipeline default e gerenciamento dos steps com botões interativos de reordenação de prioridade (**↑** / **↓**), modo de execução (`Synchronous` / `AzureBatch`), `RetryLimit`, `TimeoutSeconds` e `FallbackBehavior`.
6. **Prompts (`/Prompts/Index`, `CreateVersion`, `EditVersion`)**:
   - Gerenciamento de templates de prompt e criação/edição de versões (`SystemPrompt`, `UserPromptTemplate`, `GlossaryJson`, `SupportsPromptCache`, `PromptCacheKey`) e ativação/desativação de versões.
7. **Defaults (`/Settings/Defaults`)**:
   - Seleção rápida do `DefaultPipeline` e `DefaultPromptVersion` aplicados automaticamente aos novos arquivos detectados pelo scanner.
8. **Autenticação (`/Auth/Login` e `/Auth/Logout`)**:
   - Tela de login por token de operador com emissão de cookie `HttpOnly` de 8 horas.

---

## 8. Segurança, Saúde e Observabilidade

1. **Proteção de Credenciais**:
   - Nenhuma chave de API é salva no SQLite ou exibida na interface/logs. O banco armazena apenas o nome da variável de ambiente em `CredentialReference`, resolvida em tempo de execução por `EnvironmentCredentialResolver`.
2. **Autenticação do Dashboard e Rate Limiting**:
   - Configurada via `Dashboard:AccessToken` (`Dashboard__AccessToken`). Obrigatória em ambiente `Production` (mínimo de 32 caracteres) com validação em tempo constante (`CryptographicOperations.FixedTimeEquals`).
   - `LoginAttemptRateLimiter` limita tentativas de login por IP a 5 tentativas por minuto (retornando HTTP `429 Too Many Requests`).
3. **Endpoints de Saúde (`/health`, `/health/live`, `/health/ready`)**:
   - `/health/live`: verifica se o processo web está respondendo.
   - `/health/ready`: valida conectividade e prontidão do banco SQLite (`DatabaseReadinessHealthCheck`).
4. **Métricas Prometheus (`/metrics`)**:
   - Protegido por Bearer token separado (`Metrics:AccessToken` / `Metrics__AccessToken`).
   - Expõe contadores e gauges formatados por `PrometheusMetricsFormatter` (jobs por status, tokens processados, custo estimado total, batches ativos, saúde dos provedores e heartbeat do worker) sem expor caminhos de arquivos ou segredos.

---

## 9. Implantação em Produção (`deploy/` & `docs/deployment/`)

- **Unit File Systemd (`deploy/systemd/subtitletranslator.service`)**:
  - Configurado com diretivas de hardening (`NoNewPrivileges=true`, `PrivateTmp=true`, `ProtectSystem=strict`, `ProtectHome=true`, `ReadWritePaths` restritos ao diretório de dados `/var/lib/subtitletranslator` e diretórios de mídia).
- **Alertas Prometheus (`deploy/monitoring/subtitletranslator-alerts.yml`)**:
  - Regras prontas para monitorar indisponibilidade do worker, falhas consecutivas de provedores e acúmulo de jobs com falha.
- **Documentação de Operação (`docs/deployment/systemd.md`)**:
  - Passo a passo de publicação, permissões de diretório, configuração de variáveis de ambiente em `/etc/subtitletranslator/subtitletranslator.env`, backup do SQLite e reverse proxy.

---

## 10. Suíte de Testes Automatizados (`tests/`)

A solução conta com **67 testes automatizados** (`dotnet test`) divididos em dois projetos xUnit:

### 10.1 Testes Unitários (`tests/SubtitleTranslator.UnitTests` — 45 testes)
- **`SrtParserTests` & `SrtRoundTripTests`**: parsing de legendas, preservação de timestamps, múltiplas linhas e escrita atômica `.tmp` $\rightarrow$ `.srt`.
- **`SrtValidatorTests`**: detecção de blocos faltantes, índices divergentes, timestamps alterados e linhas vazias.
- **`SubtitleChunkerTests`**: divisão por `MaxLines`, `MaxCharacters` e `MaxTokens`.
- **`OllamaProviderTests`**: serialização de payload `/api/chat`, schema estruturado e tratamento de erros.
- **`OpenRouterProviderTests`**: headers Bearer/API Key, parse de `cached_tokens` e validação de respostas.
- **`AzureOpenAiProviderTests`**: rota de deployment, header `api-key`, `prompt_cache_key`, `cache_write_tokens`, retentativa em erro `503` e ocultação de segredos em exceções.
- **`AzureBatchProviderTests`**: geração e upload de JSONL UTF-8 com `prompt_cache_key`, criação de batch, parse de status/contadores/erros, leitura de resultados JSONL e cancelamento.
- **`TranslationProviderFactoryTests`**: resolução de `Ollama`, `OpenRouter`, `OpenAiCompatible`, `AzureOpenAi` e `AzureBatch`.
- **`TranslationCostCalculatorTests`**: cálculo de custo por 1M de tokens, desconto de tokens em cache e aplicação de `batchDiscountPercent`.
- **`EnvironmentCredentialResolverTests`**: leitura segura de variáveis de ambiente.
- **`LoginAttemptRateLimiterTests`**: bloqueio após 5 tentativas de login por minuto e expiração de janela.
- **`ProductionSecurityConfigurationValidatorTests`**: exigência de tokens fortes em produção.

### 10.2 Testes de Integração (`tests/SubtitleTranslator.IntegrationTests` — 22 testes)
- **`DatabaseMigrationTests`**: aplicação de migrations SQLite e integridade do schema.
- **`ConfigurationServicesTests`**: CRUD completo de Providers, Models, Pipelines, Steps (incluindo reordenação `MoveUp`/`MoveDown`) e Prompts (`PromptTemplate`/`PromptVersion`).
- **`OrchestratorSettingsServiceTests`**: criação automática de pipeline default e seed do prompt padrão na inicialização.
- **`TranslationPipelineExecutorTests`**: execução ponta a ponta com fallback entre steps, re-chunking de legendas pendentes, ciclo Azure Batch e escrita atômica do arquivo traduzido.
- **`TranslationRecoveryServiceTests`**: recuperação de jobs interrompidos e reconciliação de batches Azure após reinício.
- **`TranslationScannerServiceTests`**: detecção de novos arquivos `.srt`, prevenção de duplicatas e respeito à flag `ScannerPaused`.
- **`ProviderHealthCheckServiceTests`**: sondagem de saúde de provedores e validação de modelos no Ollama.
- **`DashboardQueryServiceTests`**: agregação de métricas, filtros de listagem de jobs, cálculo de duração e detalhes de tentativas/eventos.
- **`HealthEndpointsTests`**: verificação dos endpoints `/health`, `/health/live`, `/health/ready`, `/metrics` e proteção de autenticação do dashboard.
