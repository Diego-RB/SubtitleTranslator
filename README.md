# SubtitleTranslator

Orquestrador configurável para tradução automática de legendas SRT para pt-BR, desenvolvido em .NET 10 e preparado para execução em servidores Proxmox.

O projeto foi criado para substituir scripts de tradução por uma aplicação persistente, observável e configurável, com suporte a múltiplos providers, pipelines de fallback, SQLite e dashboard web.

## Estado atual

As fases iniciais já implementadas incluem:

- solução .NET 10 com ASP.NET Core Razor Pages;
- persistência com Entity Framework Core e SQLite;
- migrations e health endpoint;
- parser, writer, validator e chunker de SRT;
- abstração de providers e pipelines configuráveis;
- provider Ollama via HTTP;
- provider OpenRouter compatível com API OpenAI;
- retries, timeouts e métricas básicas nos providers;
- testes unitários e de integração.

O projeto ainda está em desenvolvimento incremental. Funcionalidades como Azure OpenAI, Azure Batch, scanner de diretórios, fallback completo, recovery e dashboard operacional serão adicionadas nas próximas fases.

## Stack

- .NET 10
- C#
- ASP.NET Core Razor Pages
- Entity Framework Core
- SQLite
- `HttpClient` e `IHttpClientFactory`
- `BackgroundService` planejado para as próximas fases
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

O endpoint de saúde está disponível em:

```text
http://localhost:5043/health
```

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
8. Reinicie a aplicação para confirmar que a configuração permanece no SQLite.

Exemplo seguro de variável de ambiente:

```text
OPENROUTER_API_KEY=<set-in-environment>
```

O banco armazena a configuração e apenas a referência da credencial; o segredo não é armazenado no SQLite.

Para Azure OpenAI síncrono, configure o provider com o endpoint do recurso, a versão da API e o `CredentialId` como nome da variável de ambiente. O model deve informar o `DeploymentName`. Nesta fase, a autenticação Azure suporta API key por variável de ambiente; Managed Identity ainda não está implementada.

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
