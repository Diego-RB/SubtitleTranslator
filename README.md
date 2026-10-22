# SubtitleTranslator

Orquestrador configurável para tradução automática de legendas SRT para pt-BR, desenvolvido em .NET 10 e preparado para execução em servidores Proxmox.

O projeto foi criado para substituir scripts de tradução por uma aplicação persistente, observável e configurável, com suporte a múltiplos providers, pipelines de fallback, SQLite e dashboard web.

## Estado atual

O runtime inclui:

- ASP.NET Core Razor Pages, EF Core e SQLite com migrations;
- parser, writer, validator e chunker SRT;
- adapters Ollama, OpenRouter, OpenAI-Compatible e Azure OpenAI síncrono, além do ciclo Azure Batch com suporte a Prompt Caching (`prompt_cache_key` e `cache_write_tokens`);
- pipelines configuráveis com fallback, retry seletivo, paralelismo configurável (`maxParallelRequests`) e prompt versionado por job;
- gerenciamento de templates e versões de prompts (**Prompts**) com seed automático de prompt padrão EN -> pt-BR;
- scanner, worker, heartbeat e recuperação de jobs/batches após restart;
- página Defaults para selecionar pipeline e prompt padrão;
- dashboard Home, listagem filtrável de jobs (com duração) e detalhes com chunks, tentativas, tokens, custos (incluindo `batchDiscountPercent`), erros e timeline;
- health endpoints para liveness, readiness (SQLite) e status agregado.

A aplicação continua em desenvolvimento. Os probes periódicos verificam endpoint/autenticação; para Ollama também confirmam models instalados. Verificação de deployment Azure e contas individuais/auditoria de operadores continuam como limitações conhecidas.

Em Development, o dashboard não exige autenticação por padrão; defina `Dashboard__AccessToken` para habilitá-la localmente. Em Production, `Dashboard__AccessToken` e `Metrics__AccessToken` são obrigatórios e devem ter pelo menos 32 caracteres. O primeiro protege as páginas com cookie HttpOnly de oito horas; o segundo protege `/metrics` via Bearer. O endpoint de login aceita até cinco tentativas por IP confiável a cada minuto. Os health endpoints permanecem públicos para sondas de infraestrutura.

## Stack

- .NET 10
- C#
- ASP.NET Core Razor Pages
- Entity Framework Core
- SQLite
- `HttpClient` e `IHttpClientFactory`
- `BackgroundService` para scanner, recuperação e execução
- xUnit

## Estrutura

```text
SubtitleTranslator.slnx
├── src/SubtitleTranslator
│   ├── Application
│   ├── Domain
│   ├── Infrastructure
│   ├── Srt
│   └── Web (Razor Pages)
└── tests
    ├── SubtitleTranslator.UnitTests
    └── SubtitleTranslator.IntegrationTests
```

## Requisitos

- .NET SDK 10
- Windows, Linux ou ambiente compatível com ASP.NET Core
- Ollama opcional para tradução local
- Credencial do OpenRouter opcional para tradução em nuvem

## Executar localmente

Na raiz do projeto, restaure as dependências e execute a aplicação:

```powershell
dotnet restore
dotnet run --project src/SubtitleTranslator/SubtitleTranslator.csproj
```

O perfil HTTP de desenvolvimento usa a porta `5043`:

```text
http://localhost:5043
```

Os endpoints de saúde estão disponíveis em:

```text
http://localhost:5043/health
http://localhost:5043/health/live
http://localhost:5043/health/ready
http://localhost:5043/metrics
```

`/health/live` indica que o processo web está atendendo; `/health/ready` valida a conexão com SQLite. `/health` mantém o check agregado. Esses endpoints permanecem públicos para probes de infraestrutura.
`/metrics` expõe métricas agregadas em formato Prometheus e exige `Authorization: Bearer <token>` quando configurado; em Production, a configuração do token é obrigatória. A saída não contém caminhos de arquivos, nomes de models ou credenciais.

Também é possível iniciar o projeto pelo VS Code usando a configuração `SubtitleTranslator: Debug` em `.vscode/launch.json`.

## Testes

Execute todos os testes da solução:

```powershell
dotnet test
```

Para validar manualmente scanner, providers, pipelines, fallback, retry e endpoints, consulte o [roteiro de teste manual](docs/testing/manual-test-guide.md).

## Configuração de credenciais

Credenciais não devem ser incluídas no código, em texto puro no SQLite ou no Git. Na interface, cole a chave no campo **Credential**; ela é protegida com ASP.NET Core Data Protection antes de ser armazenada. O key ring do Data Protection deve persistir e ser protegido/backup junto com o banco. Referências antigas a variáveis de ambiente continuam disponíveis em **Advanced → Credential ID**.

Para o modo legado de variável de ambiente, `CredentialId` representa o nome da variável. Por exemplo:

```text
OPENROUTER_API_KEY=sua-chave-aqui
```

O arquivo `.env` local é ignorado pelo Git. Use um mecanismo seguro de secrets no ambiente de produção.

## Configuração pela interface

1. Inicie a aplicação.
2. Abra a página **Providers**.
3. Crie um provider e cole a chave no campo **Credential**. Opcionalmente, use **Advanced → Credential ID** para uma variável de ambiente.
4. Crie um model associado ao provider (incluindo tarifas por 1M tokens e configuração JSON opcional para `batchDiscountPercent` ou `maxParallelRequests`).
5. Configure a estratégia e os limites de chunking.
6. Crie um pipeline e ordene os steps usando os botões de prioridade (↑ / ↓).
7. Em **Prompts**, gerencie templates, crie novas versões com glossário e configure `SupportsPromptCache` / `PromptCacheKey`.
8. Em **Defaults**, escolha o pipeline e a versão de prompt para novos jobs.
9. Confirme que os diretórios monitorados estão corretos na seção `Scanner` do `appsettings.json`.

O worker aguarda `Scanner:FileStabilityDelaySeconds` (15 segundos por padrão quando não configurado) após a última gravação observada antes de iniciar um job, reduzindo falhas causadas por arquivos ainda sendo copiados. Jobs `Failed` ou `Cancelled` podem ser re-enfileirados pela página de detalhes; o retry preserva resultados já concluídos e o histórico de tentativas. Se a saída de um job concluído for apagada e a fonte não tiver mudado, o scanner reabre o job para restaurar a saída usando chunks persistidos.

Exemplo seguro de variável de ambiente:

```text
OPENROUTER_API_KEY=<set-in-environment>
```

Quando uma chave é colada no formulário, o SQLite armazena somente o valor protegido pelo Data Protection, não a chave em texto puro. O key ring é necessário para descriptografar a credencial após reinícios ou restaurações.

Para Azure OpenAI síncrono via API v1, configure o provider com o endpoint terminado em `/openai/v1`, cole a chave em **Credential**, deixe `ApiVersion` vazio e informe `DeploymentName`. O adapter usa Chat Completions. O modo legado continua aceitando endpoint de deployment e `ApiVersion`. Azure suporta `ApiKey` e `AzureKeyCredential`; Managed Identity ainda não está implementada.

Para Azure Batch, use o mesmo provider Azure OpenAI e um model com `SupportsBatch` habilitado e `DeploymentName` configurado. O fluxo JSONL, submissão, polling, outputs/errors, persistência de estado e recuperação são executados pelo worker.

O executor usa o `PipelineId` e `PromptVersionId` fixados no job. Cada step usa sua própria política de chunks; se um provider falhar, somente legendas ainda não traduzidas avançam para o próximo step. A saída `.pt-BR.srt` é criada atomicamente após validação completa. O worker detecta arquivos, recupera jobs interrompidos e mantém heartbeat.

Opções avançadas via JSON na configuração do model (`ConfigurationJson`):

```json
{
    "pipelineMaxAttempts": 2,
    "pipelineRetryDelayMilliseconds": 1000,
    "maxParallelRequests": 2,
    "batchDiscountPercent": 50
}
```

O padrão é uma tentativa; o número configurado é limitado a cinco. Tentativas, tokens (incluindo `cached_tokens` e `cache_write_tokens`), latência e custo estimado são registrados no SQLite por chunk. A versão do prompt é fixada no job para que uma retomada use o mesmo texto/glossário configurado.

## Executar em Linux com systemd

Há um unit file e um guia de implantação em [`deploy/systemd/subtitletranslator.service`](deploy/systemd/subtitletranslator.service) e [`docs/deployment/systemd.md`](docs/deployment/systemd.md). O serviço usa `/var/lib/subtitletranslator` para o SQLite, escuta por padrão em `127.0.0.1:8080` e deve ficar atrás de um reverse proxy com TLS. Em Production, o dashboard exige um token de operador e `/metrics` exige um bearer token separado. Regras Prometheus estão em [`deploy/monitoring/subtitletranslator-alerts.yml`](deploy/monitoring/subtitletranslator-alerts.yml).

Para execução local, o banco padrão é `subtitletranslator.db` relativo ao diretório de trabalho. Em produção, sobrescreva `ConnectionStrings__Default` com um caminho persistente gravável pelo usuário do serviço.

## Princípios do projeto

- configuração em vez de hardcode;
- providers extensíveis;
- timestamps SRT controlados pela aplicação;
- persistência e recuperação após reinício;
- validação antes da geração do arquivo final;
- logs sem credenciais ou headers de autorização;
- fallback pertencente ao pipeline, não aos providers;
- execução simples em servidor Proxmox.

## Licença

Este projeto é distribuído sob a [Licença MIT](LICENSE).

As bibliotecas de terceiros incluídas no projeto permanecem sob suas respectivas licenças. Os arquivos de licença distribuídos com bibliotecas frontend, como Bootstrap e jQuery, devem ser mantidos quando esses componentes forem redistribuídos.
