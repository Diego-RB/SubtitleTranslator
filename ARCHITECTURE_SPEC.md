# Subtitle Translator — Especificação Técnica

## 1. Objetivo
Construir uma aplicação .NET 10/C# para tradução automática de SRT no Proxmox, altamente configurável e observável.

A aplicação deve:
- monitorar diretórios;
- detectar SRT ainda não traduzidos;
- criar jobs persistentes;
- dividir legendas em chunks;
- suportar OpenRouter, Azure OpenAI e Ollama;
- suportar Azure Batch;
- permitir fallback configurável por modelo/chunk;
- validar a saída SRT;
- gerar `.pt-BR.srt`;
- persistir estado em SQLite;
- sobreviver a reinícios;
- oferecer dashboard web;
- registrar tokens, custo, latência, retries e erros;
- executar como serviço systemd.

## 2. Contexto do sistema atual
O script legado monitora `/media/streaming` e `/media/qbittorrent`, usa OpenRouter como primeira camada, Azure como segunda e Ollama como terceira. Ele usa chunks grandes na nuvem e chunks de 1 bloco no Ollama para proteger a RX 5700 XT 8 GB.

A nova aplicação deve preservar esse objetivo, mas deixar providers, modelos, prompts, pipelines, chunking e políticas configuráveis em banco.

## 3. Stack
- .NET 10 LTS
- C#
- ASP.NET Core
- BackgroundService
- Entity Framework Core
- SQLite
- Razor Pages
- HttpClient / IHttpClientFactory
- Microsoft.Extensions.Options
- ILogger
- Dependency Injection

Não usar inicialmente Redis, RabbitMQ, PostgreSQL, Kubernetes, Node.js ou microserviços.

## 4. Arquitetura

```text
ASP.NET Core
├── Dashboard
└── Worker
    └── Translation Pipeline
        ├── OpenRouter Provider
        ├── Azure OpenAI Provider
        └── Ollama Provider
                 ↓
             Validator
                 ↓
             SRT Writer

SQLite
├── Providers
├── Credentials references
├── Models
├── Deployments
├── Pipelines
├── Jobs
├── Chunks
└── Attempts
```

## 5. Conceitos
### Provider
Mecanismo de acesso: OpenRouter, Azure OpenAI, Ollama ou futuro OpenAI-compatible.

Campos:
- Id
- Name
- Type
- BaseUrl
- AuthenticationType
- CredentialId
- ApiVersion
- Enabled
- CreatedAt
- UpdatedAt

### Model
Modelo lógico que pode ser usado em um provider.

Campos:
- Id
- ProviderId
- Name
- ModelId
- DeploymentName
- Enabled
- Priority
- SupportsBatch
- SupportsStreaming
- SupportsTranslation
- ContextWindow
- MaxOutputTokens
- Temperature
- TopP
- InputCostPer1M
- OutputCostPer1M
- CachedInputCostPer1M
- ChunkStrategy
- MaxChunkTokens
- MaxChunkSubtitles
- ConfigurationJson

Para Azure, manter modelo e deployment como conceitos separados.

## 6. Abstração de providers
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

Implementações:
- OpenAiCompatibleProvider
- AzureOpenAiProvider
- OllamaProvider

A lógica de fallback pertence ao pipeline, não ao provider.

## 7. Pipeline configurável
Não hardcodar:
`OpenRouter -> Azure -> Ollama`.

Persistir Pipeline e PipelineStep.

Exemplo:

```text
Tradução Padrão
1. OpenRouter / Gemma
2. OpenRouter / Nemotron
3. Azure / GPT-5.6 Luna
4. Azure / GPT-4o-mini
5. Ollama / Qwen 2.5 7B
```

Permitir criar outros pipelines:
- Economia
- Qualidade
- Testes

Permitir ativar, desativar e reordenar steps no dashboard.

## 8. Domínio SRT
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

O parser deve converter SRT para objetos. O writer reconstrói SRT.

A IA deve preferencialmente receber `ID + texto`, e não ser responsável por devolver timestamps. O software controla:
- índice;
- início;
- fim;
- ordem.

## 9. Chunking
Chunking deve ser configurável por modelo/pipeline.

Estratégias:
- `MaxTokens`
- `MaxSubtitleCount`

Sugestão inicial:
- Azure/OpenRouter: 8.000–12.000 tokens por chunk;
- Ollama: 1 subtitle por chamada inicialmente, podendo testar 2–5.

Nunca reutilizar automaticamente o tamanho do chunk de nuvem no Ollama, pois o nó local possui RX 5700 XT de 8 GB e Qwen 2.5 7B.

## 10. Azure Batch
Batch é assíncrono:
1. gerar JSONL;
2. enviar input;
3. criar batch;
4. salvar BatchId;
5. monitorar;
6. obter output;
7. obter errors;
8. atualizar chunks;
9. retry somente dos chunks que falharam.

Persistir:
- BatchId
- Status
- CreatedAt
- InProgressAt
- ExpiresAt
- CompletedAt
- OutputFileId
- ErrorFileId
- TotalRequests
- CompletedRequests
- FailedRequests
- LastPolledAt
- LastProgressAt

Estados importantes:
`validating`, `failed`, `in_progress`, `finalizing`, `completed`, `expired`, `cancelling`, `cancelled`.

Polling inicial: 60 segundos.

Criar alertas para stall, mas não assumir falha apenas por ausência breve de progresso.

## 11. Fallback seletivo
Se 100 chunks resultarem em 97 sucesso e 3 falhas:
- preservar os 97;
- retry somente os 3.

Política possível:
`OpenRouter -> Azure GPT-5.6 Luna -> Azure GPT-4o-mini -> Ollama`.

## 12. Prompt e cache
Prompts devem ser configuráveis e versionados.

Separar:
- PromptTemplate
- PromptVersion
- texto estático
- exemplos
- glossário

Para Azure:
- conteúdo estático/reutilizável no início;
- conteúdo dinâmico no final;
- suportar `prompt_cache_key` e cache breakpoints para GPT-5.6+;
- registrar `cached_tokens` e `cache_write_tokens` quando disponíveis;
- permitir desativar cache.

## 13. Validação
Antes de gerar o arquivo final:
- mesma quantidade de legendas;
- mesmos índices;
- nenhum índice duplicado;
- nenhum índice faltando;
- timestamps idênticos;
- nenhuma tradução vazia;
- ordem correta;
- UTF-8 válido.

Falha de validação deve disparar retry/fallback.

## 14. Persistência
Usar EF Core + SQLite.

### TranslationJob
- Id
- SourcePath
- OutputPath
- Status
- PipelineId
- TotalChunks
- CompletedChunks
- FailedChunks
- CurrentProvider
- CurrentModel
- AzureBatchId
- CreatedAt
- StartedAt
- UpdatedAt
- CompletedAt
- LastActivityAt
- LastError
- RetryCount
- InputTokens
- OutputTokens
- EstimatedCost

### TranslationChunk
- Id
- JobId
- Sequence
- StartSubtitleIndex
- EndSubtitleIndex
- Status
- Provider
- Model
- Attempt
- InputTokens
- OutputTokens
- LatencyMs
- Error
- CreatedAt
- UpdatedAt
- CompletedAt

### TranslationAttempt
Registrar cada tentativa individual.

## 15. Estados
Job:
`Discovered, Preparing, Queued, OpenRouterProcessing, AzureBatchSubmitting, AzureBatchProcessing, AzureBatchFinalizing, Retrying, LocalProcessing, Validating, Completed, Failed, Cancelled`

Chunk:
`Pending, Processing, Completed, Failed, RetryPending, Cancelled`

## 16. Recovery
O estado nunca pode depender somente da memória do processo.

Manter heartbeat e última atividade.

Após restart:
1. carregar jobs incompletos;
2. identificar BatchId;
3. consultar Azure;
4. continuar monitoramento;
5. não duplicar chunks;
6. retomar pendentes;
7. tratar chunks presos em Processing como recuperáveis.

## 17. Scanner
Configuração:

```json
{
  "Scanner": {
    "Enabled": true,
    "IntervalSeconds": 30,
    "Directories": [
      "/media/streaming",
      "/media/qbittorrent"
    ]
  }
}
```

O scanner apenas descobre/enfileira; não chama providers diretamente.

Ignorar:
- `.pt-BR.srt`
- `.pt.srt`

Evitar jobs duplicados.

## 18. Dashboard
Razor Pages.

### Home
- sistema online/offline;
- jobs ativos;
- fila;
- concluídos hoje;
- falhas;
- stalled jobs;
- status dos providers.

### Jobs
- arquivo;
- status;
- progresso;
- provider/model;
- tempo;
- custo.

### Job Details
- JobId;
- arquivo;
- pipeline;
- BatchId;
- progresso;
- chunks;
- tentativas;
- tokens;
- custos;
- erros;
- timeline.

### Providers
- provider;
- tipo;
- endpoint;
- status;
- modelos;
- último health check.

### Models
- provider;
- modelo;
- capabilities;
- prioridade;
- chunk policy;
- preço;
- status.

### Pipelines
Editor para adicionar, remover, ativar/desativar e reordenar steps.

## 19. Health Checks
- OpenRouter: endpoint/auth;
- Azure: endpoint/deployment/auth;
- Ollama: `/api/tags` e confirmação do modelo.

Estados:
`ONLINE`, `OFFLINE`, `DEGRADED`, `UNKNOWN`.

## 20. Observabilidade
Usar `ILogger<T>`.

Registrar:
- JobId
- ChunkId
- Provider
- Model
- BatchId
- Event
- ErrorCode

Métricas:
- requests;
- success/failure;
- retry count;
- latency;
- input/output tokens;
- cached tokens;
- cache write tokens;
- cost;
- throughput.

## 21. Custos
Preços são configuração:
- InputCostPer1M
- OutputCostPer1M
- CachedInputCostPer1M
- BatchDiscountPercent

Nunca hardcodar preços na regra de negócio.

## 22. Segurança
Não colocar API keys no código, Git ou logs.

Usar environment variables/secrets.

As credenciais expostas no script legado devem ser consideradas comprometidas e rotacionadas.

## 23. Testes
Unitários:
- parser;
- writer;
- validator;
- chunker;
- custo;
- pipeline;
- retry.

Integração:
- SQLite;
- mock OpenRouter;
- mock Azure;
- mock Ollama;
- ciclo de Batch simulado.

Casos críticos:
- Batch com sucessos e falhas parciais;
- Batch expired;
- restart durante Batch;
- Ollama offline;
- OpenRouter 429;
- Azure timeout/5xx;
- resultado com quantidade incorreta;
- timestamp alterado;
- duas instâncias processando mesmo arquivo.

## 24. Concorrência
Inicialmente:
- 1 job por arquivo;
- Ollama: 1 request concorrente;
- OpenRouter: limite configurável;
- Azure Batch: agrupar requests.

## 25. Hosting
Executar como serviço systemd.

Exemplo conceitual:

```ini
[Service]
Restart=always
RestartSec=30
```

Dashboard inicialmente na porta 8080.

## 26. Critérios de aceite
A v1 deve:
1. detectar SRT;
2. criar job;
3. persistir SQLite;
4. parsear SRT;
5. criar chunks;
6. usar pipeline configurado;
7. integrar OpenRouter;
8. integrar Azure Batch;
9. monitorar Batch;
10. coletar outputs/errors;
11. retry seletivo;
12. usar Ollama;
13. validar SRT;
14. gerar `.pt-BR.srt`;
15. sobreviver a restart;
16. mostrar estado no dashboard;
17. mostrar tokens/custo;
18. executar via systemd;
19. manter segredos fora do source.

## 27. Princípios
- configuração em vez de hardcode;
- jobs e chunks como entidades;
- persistência;
- idempotência;
- observabilidade;
- extensibilidade;
- segurança;
- simplicidade operacional.
