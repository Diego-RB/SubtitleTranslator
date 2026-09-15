# Prompt Mestre para GitHub Copilot — Subtitle Translation Orchestrator

Você é o principal engenheiro de software responsável por implementar uma aplicação chamada `SubtitleTranslator`.

O usuário desenvolve em .NET/C# e deseja substituir o script atual por um Translation Orchestrator robusto, altamente configurável e fácil de monitorar.

## Objetivo

Criar uma aplicação .NET 10 LTS / C# / ASP.NET Core que monitore SRTs e os traduza para pt-BR usando providers/modelos configuráveis.

A aplicação deve suportar:
- OpenRouter;
- Azure OpenAI;
- Azure Batch;
- Ollama local;
- fallback configurável;
- SQLite;
- dashboard Razor Pages;
- BackgroundService;
- systemd;
- parser/writer/validator SRT;
- métricas e custos;
- recovery após restart.

## Regra principal

NUNCA hardcode no pipeline:
- OpenRouter;
- Azure;
- Ollama;
- nomes de modelos;
- ordem de fallback;
- prompts;
- preços;
- endpoints.

Provider, Model, Deployment, Credential reference, Pipeline e PipelineStep são configuração persistida.

Adicionar um modelo deve ser possível sem recompilar.

Adicionar um provider deve exigir adapter novo apenas quando realmente houver protocolo novo.

## Stack

Use:
- .NET 10 LTS
- ASP.NET Core
- C#
- BackgroundService
- EF Core
- SQLite
- Razor Pages
- HttpClient/IHttpClientFactory
- ILogger
- DI nativo

Não introduza inicialmente:
- Node.js
- Redis
- RabbitMQ
- PostgreSQL
- Kubernetes
- microservices

## Estrutura sugerida

```text
SubtitleTranslator/
├── SubtitleTranslator.sln
├── src/
│   └── SubtitleTranslator/
│       ├── Program.cs
│       ├── appsettings.json
│       ├── Domain/
│       │   ├── Entities/
│       │   ├── Enums/
│       │   └── ValueObjects/
│       ├── Application/
│       │   ├── Interfaces/
│       │   ├── Services/
│       │   ├── Pipelines/
│       │   └── Policies/
│       ├── Infrastructure/
│       │   ├── Persistence/
│       │   ├── Providers/
│       │   ├── Http/
│       │   └── Monitoring/
│       ├── Srt/
│       │   ├── Parser/
│       │   ├── Writer/
│       │   ├── Validation/
│       │   └── Chunking/
│       ├── Workers/
│       └── Web/
│           ├── Pages/
│           ├── Components/
│           └── Models/
└── tests/
    ├── SubtitleTranslator.UnitTests/
    └── SubtitleTranslator.IntegrationTests/
```

Pode simplificar essa estrutura quando uma pasta não tiver responsabilidade real. Não crie abstrações vazias.

## Provider abstraction

Criar:

```csharp
public interface ITranslationProvider
{
    string Name { get; }

    Task<TranslationResult> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken = default);
}
```

Para Batch:

```csharp
public interface IBatchTranslationProvider
{
    Task<BatchSubmissionResult> SubmitBatchAsync(...);
    Task<BatchStatusResult> GetStatusAsync(...);
    Task<BatchResult> GetResultsAsync(...);
    Task<BatchErrorResult> GetErrorsAsync(...);
    Task CancelBatchAsync(...);
}
```

Implementar:
- OpenAiCompatibleProvider;
- AzureOpenAiProvider;
- OllamaProvider.

Fallback não pertence aos providers.

## Provider e Model

Provider:
```text
Id
Name
Type
BaseUrl
AuthenticationType
CredentialId
ApiVersion
Enabled
```

Model:
```text
Id
ProviderId
Name
ModelId
DeploymentName
Enabled
Priority
SupportsBatch
SupportsStreaming
SupportsTranslation
ContextWindow
MaxOutputTokens
Temperature
TopP
InputCostPer1M
OutputCostPer1M
CachedInputCostPer1M
ChunkStrategy
MaxChunkTokens
MaxChunkSubtitles
ConfigurationJson
```

## Pipeline

Persistir Pipeline e PipelineStep.

Exemplo:
```text
OpenRouter/Gemma
Azure/GPT-5.6 Luna
Azure/GPT-4o-mini
Ollama/Qwen 2.5 7B
```

Permitir reordenar e ativar/desativar no dashboard.

## SRT

Criar:

```csharp
public sealed class Subtitle
{
    public required int Index { get; init; }
    public required TimeSpan Start { get; init; }
    public required TimeSpan End { get; init; }
    public required string Text { get; init; }
}
```

O parser deve converter SRT em objetos.

A IA deve receber preferencialmente `ID + texto`.

Não permitir que o modelo seja responsável por timestamps.

O software reconstrói os timestamps.

## Chunking

Criar estratégias:
- MaxTokens;
- MaxSubtitleCount.

Inicial:
- nuvem: 8.000–12.000 input tokens;
- Ollama: 1 subtitle inicialmente.

O chunk da nuvem NUNCA deve ser enviado automaticamente inteiro ao Ollama.

## Azure Batch

Implementar fluxo:
1. montar requests;
2. gerar JSONL;
3. upload;
4. criar Batch;
5. salvar BatchId;
6. polling;
7. output/error retrieval;
8. atualizar chunks;
9. retry seletivo.

Suportar status:
- validating;
- failed;
- in_progress;
- finalizing;
- completed;
- expired;
- cancelling;
- cancelled.

Polling de 60s inicialmente.

Persistir status e timestamps do Batch.

Implementar stall warning e expiration handling.

## OpenRouter

Usar adapter OpenAI-compatible quando adequado.

Suportar:
- 429;
- 5xx;
- timeout;
- conexão interrompida;
- retry/backoff.

O catálogo de modelos deve ser configuração/dados, não código.

## Ollama

Usar HTTP.

Configuração:
```text
BaseUrl
Model
Timeout
MaxParallelRequests
ChunkStrategy
MaxSubtitleCount
Temperature
TopP
NumPredict
```

Máximo inicial: 1 request concorrente.

O host atual do Ollama é configurável; não hardcode o endereço do script legado.

## Persistência

EF Core + SQLite.

Criar:
- TranslationJob;
- TranslationChunk;
- TranslationAttempt;
- Provider;
- ModelDefinition;
- Deployment quando aplicável;
- Pipeline;
- PipelineStep;
- PromptTemplate/PromptVersion;
- Credential reference.

## Job state

Use enum:
```text
Discovered
Preparing
Queued
OpenRouterProcessing
AzureBatchSubmitting
AzureBatchProcessing
AzureBatchFinalizing
Retrying
LocalProcessing
Validating
Completed
Failed
Cancelled
```

Chunk:
```text
Pending
Processing
Completed
Failed
RetryPending
Cancelled
```

## Recovery

Persistir:
- LastHeartbeatAt;
- LastActivityAt;
- BatchId;
- progresso.

Após restart:
- carregar jobs incompletos;
- consultar Batch existente;
- continuar;
- não duplicar trabalho.

## Scanner

Monitorar diretórios configuráveis.

Ignorar:
- `.pt-BR.srt`;
- `.pt.srt`.

Scanner apenas descobre e cria jobs.

## Validator

Validar:
- mesma quantidade de legendas;
- mesmos ids;
- sem duplicidade;
- sem ids faltantes;
- timestamps idênticos;
- texto não vazio;
- ordem correta.

Falha de validação deve permitir retry/fallback.

## Prompt

Prompts configuráveis e versionados.

Regras iniciais equivalentes ao legado:
- inglês -> pt-BR;
- tradução natural;
- não resumir;
- não cortar falas;
- nomes próprios;
- preservar sentido;
- retorno somente no formato esperado.

Para Azure GPT-5.6+:
- manter prefixo estável no início;
- conteúdo dinâmico no fim;
- suporte a prompt cache key/breakpoint;
- registrar cached_tokens/cache_write_tokens.

Não hardcode prompt dentro do provider.

## Dashboard

Razor Pages.

Home:
- online/offline;
- jobs ativos;
- fila;
- concluídos;
- falhas;
- stalled;
- providers.

Jobs:
- arquivo;
- status;
- progresso;
- provider;
- model;
- duração;
- custo.

Job details:
- BatchId;
- chunks;
- retries;
- tokens;
- custos;
- erros;
- timeline.

Providers:
- configuração sanitizada;
- status;
- models;
- health check.

Models:
- provider;
- model;
- capabilities;
- chunk policy;
- custo;
- status.

Pipelines:
- editar;
- adicionar/remover steps;
- reordenar;
- ativar/desativar.

## Observabilidade

Usar ILogger<T> e logs estruturados.

Nunca logar API keys, Authorization headers ou secrets.

Registrar:
- JobId;
- ChunkId;
- Provider;
- Model;
- BatchId;
- Event;
- ErrorCode.

Métricas:
- input tokens;
- output tokens;
- cached tokens;
- cache write tokens;
- latency;
- retry count;
- custo;
- sucesso/falha.

## Segurança

NÃO copie credenciais do script legado.

Use environment variables/secrets.

As credenciais antigas devem ser tratadas como comprometidas e rotacionadas.

## Concorrência

Inicial:
- um job por arquivo;
- Ollama 1 request;
- OpenRouter concurrency configurável;
- Azure usa Batch.

## Testes

Criar unit/integration tests para:
- parser;
- writer;
- validator;
- chunker;
- custo;
- pipeline;
- fallback;
- SQLite;
- providers mockados;
- Batch lifecycle;
- recovery.

Cenários obrigatórios:
- Batch parcial;
- Batch expired;
- restart;
- OpenRouter 429;
- Azure 5xx/timeout;
- Ollama offline;
- resultado inválido;
- timestamp alterado;
- duplicação de job.

## Desenvolvimento incremental

NÃO gere toda a aplicação de uma vez.

Implemente em fases.

### Fase 1
- solution;
- ASP.NET Core;
- DI;
- logging;
- SQLite;
- EF Core;
- entidades;
- migrations;
- health endpoint;
- página inicial.

Depois compile e rode os testes.

### Fase 2
- parser;
- writer;
- validator;
- chunker;
- testes.

### Fase 3
- provider abstraction;
- factory;
- model/pipeline configuration.

### Fase 4
- Ollama.

### Fase 5
- OpenRouter.

### Fase 6
- Azure síncrono.

### Fase 7
- Azure Batch.

### Fase 8
- pipeline/fallback/retry.

### Fase 9
- recovery/watchdog/heartbeat.

### Fase 10
- dashboard completo.

### Fase 11
- systemd e documentação operacional.

Ao final de cada fase:
```text
Implemented:
- ...

Tests:
- ...

Known limitations:
- ...

Next phase:
- ...
```

Não avance se a fase atual não compilar e os testes relevantes não passarem.

## Primeira ação

Comece APENAS pela Fase 1.

Não implemente providers ainda.

Crie uma aplicação executável em .NET 10 LTS com:
- SQLite/EF Core;
- estrutura inicial;
- logging;
- DI;
- health endpoint;
- migrations;
- página inicial;
- testes iniciais.

Compile e execute os testes antes de prosseguir.
