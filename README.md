# SubtitleTranslator

Orquestrador configurável para tradução automática de legendas SRT para pt-BR, desenvolvido em .NET 10 e preparado para execução em servidores Proxmox.

O projeto foi criado para substituir scripts de tradução por uma aplicação persistente, observável e configurável, com suporte a múltiplos providers, pipelines de fallback, SQLite e dashboard web.

## Estado atual

O runtime inclui:

- ASP.NET Core Razor Pages, EF Core e SQLite com migrations;
- parser, writer, validator e chunker SRT;
- adapters Ollama, OpenRouter e Azure OpenAI síncrono, além do ciclo Azure Batch;
- pipelines configuráveis com fallback, retry seletivo e prompt versionado por job;
- scanner, worker, heartbeat e recuperação de jobs/batches após restart;
- página Defaults para selecionar pipeline e prompt padrão;
- dashboard Home, listagem filtrável de jobs e detalhes com chunks, tentativas, tokens, custos, erros e timeline;
- health endpoints para liveness, readiness (SQLite) e status agregado.

A aplicação continua em desenvolvimento. Os probes periódicos verificam endpoint/autenticação; para Ollama também confirmam models instalados. Verificação de deployment Azure e contas individuais/auditoria de operadores continuam como limitações conhecidas.

Em Development, o dashboard não exige autenticação por padrão; defina `Dashboard__AccessToken` para habilitá-la localmente. Em Production, `Dashboard__AccessToken` e `Metrics__AccessToken` são obrigatórios e devem ter pelo menos 32 caracteres. O primeiro protege as páginas com cookie HttpOnly de oito horas; o segundo protege `/metrics` via Bearer. Os health endpoints permanecem públicos para sondas de infraestrutura.

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

## Configuração de credenciais

As credenciais não devem ser incluídas no código, no SQLite ou no Git.

O campo `CredentialId` representa o nome de uma variável de ambiente. Por exemplo:

```text
OPENROUTER_API_KEY=sua-chave-aqui
```

O arquivo `.env` local é ignorado pelo Git. Use um mecanismo seguro de secrets no ambiente de produção.

## Configuração pela interface

1. Inicie a aplicação.
2. Abra a página **Providers**.
3. Crie um provider e informe o `CredentialId` como nome da variável de ambiente.
4. Crie um model associado ao provider.
5. Configure a estratégia e os limites de chunking.
6. Crie um pipeline.
7. Adicione e ordene os pipeline steps usando os models configurados.
8. Em **Defaults**, escolha o pipeline e a versão de prompt para novos jobs.
9. Confirme que os diretórios monitorados estão corretos na seção `Scanner` do `appsettings.json`.

Exemplo seguro de variável de ambiente:

```text
OPENROUTER_API_KEY=<set-in-environment>
```

O banco armazena a configuração e apenas a referência da credencial; o segredo não é armazenado no SQLite.

Para Azure OpenAI síncrono, configure o provider com o endpoint do recurso, a versão da API e o `CredentialId` como nome da variável de ambiente. O model deve informar o `DeploymentName`. Nesta fase, a autenticação Azure suporta API key por variável de ambiente; Managed Identity ainda não está implementada.

Para Azure Batch, use o mesmo provider Azure OpenAI e um model com `SupportsBatch` habilitado e `DeploymentName` configurado. O fluxo JSONL, submissão, polling, outputs/errors, persistência de estado e recuperação são executados pelo worker.

O executor usa o `PipelineId` e `PromptVersionId` fixados no job. Cada step usa sua própria política de chunks; se um provider falhar, somente legendas ainda não traduzidas avançam para o próximo step. A saída `.pt-BR.srt` é criada atomicamente após validação completa. O worker detecta arquivos, recupera jobs interrompidos e mantém heartbeat.

Retries síncronos adicionais são opt-in na configuração do model:

```json
{
    "pipelineMaxAttempts": 2,
    "pipelineRetryDelayMilliseconds": 1000
}
```

O padrão é uma tentativa; o número configurado é limitado a cinco. Tentativas, tokens, latência e custo estimado são registrados no SQLite por chunk. A versão do prompt é fixada no job para que uma retomada use o mesmo texto/glossário configurado.

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
