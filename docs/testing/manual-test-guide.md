# Roteiro de teste manual — SubtitleTranslator

Este roteiro testa a aplicação em **Development**, sem apontar o scanner para bibliotecas de mídia reais. A tradução completa precisa de um provider funcional (Ollama local ou provider de nuvem); health, descoberta, dashboard e banco podem ser testados sem credenciais de tradução.

## 1. Pré-requisitos e ambiente isolado

1. Instale/valide o .NET SDK 10:

   ```powershell
   dotnet --version
   ```

2. Na raiz do repositório, crie uma pasta temporária para os SRTs:

   ```powershell
   New-Item -ItemType Directory -Force C:\Temp\SubtitleTranslator\Input | Out-Null
   ```

3. Edite `src/SubtitleTranslator/appsettings.Development.json` e acrescente uma seção `Scanner` como abaixo. Preserve as configurações `DetailedErrors` e `Logging` que já existem. Use apenas a pasta temporária; o scanner percorre subpastas recursivamente.

   ```json
   "Scanner": {
     "Enabled": true,
     "IntervalSeconds": 10,
     "FileStabilityDelaySeconds": 5,
     "RecoveryIntervalSeconds": 20,
     "StalledJobThresholdMinutes": 3,
     "HeartbeatIntervalSeconds": 5,
     "Directories": [
       "C:\\Temp\\SubtitleTranslator\\Input"
     ]
   }
   ```

4. Para manter o SQLite de teste isolado, abra um PowerShell na raiz e defina uma conexão temporária antes de iniciar:

   ```powershell
   $env:ConnectionStrings__Default = 'Data Source=C:\Temp\SubtitleTranslator\subtitletranslator-dev.db'
   ```

   Em Development, dashboard e `/metrics` não exigem tokens se `Dashboard__AccessToken` e `Metrics__AccessToken` não estiverem definidos. Não grave chaves reais no JSON, no SQLite ou neste roteiro.

## 2. Compilar, testar e iniciar

1. Na raiz do repositório, rode a suíte automatizada:

   ```powershell
   dotnet test --no-restore --verbosity minimal
   ```

   Resultado esperado: build concluído e todos os testes aprovados.

2. Inicie **o projeto web**, não a solução. Rodar apenas `dotnet run` na raiz pode falhar porque o executável está na pasta `src`:

   ```powershell
   dotnet run --project src/SubtitleTranslator/SubtitleTranslator.csproj --launch-profile http
   ```

3. No primeiro startup, a aplicação aplica as migrations com `Database.Migrate()` e cria o prompt padrão EN → pt-BR. Aguarde as mensagens de startup no terminal.

4. Abra `http://localhost:5043`. A Home deve carregar; o indicador deve mostrar `WORKER ONLINE` após o worker iniciar.

5. Em outro PowerShell, teste os health endpoints:

   ```powershell
   Invoke-WebRequest http://localhost:5043/health/live
   Invoke-WebRequest http://localhost:5043/health/ready
   Invoke-WebRequest http://localhost:5043/health
   ```

   Esperado: HTTP 200 e status saudável. O `/health/ready` também confirma acesso ao SQLite.

> Se `dotnet run` encerrar com erro, use a primeira exceção completa do terminal para diagnosticar. Não presuma que a migration falhou: configuração ou resolução do projeto podem interromper o startup antes dela.

## 3. Configurar uma tradução local com Ollama

Esta é a forma recomendada de testar uma tradução sem chave de API.

1. Instale/inicie o Ollama e baixe um modelo compatível com o hardware. Confirme o nome exato:

   ```powershell
   ollama list
   ```

2. Confirme que a API responde:

   ```powershell
   Invoke-RestMethod http://localhost:11434/api/tags
   ```

3. Na aplicação, acesse **Providers → Create** e cadastre:
   - Name: `Ollama local`
   - Type: `Ollama`
   - Base URL: `http://localhost:11434`
   - Authentication: `None`
   - Enabled: marcado

4. Em **Models → Create**, crie um model associado ao provider:
   - Name: um nome amigável;
   - Model ID: exatamente o nome exibido por `ollama list`;
   - Enabled e SupportsTranslation: marcados;
   - Chunk strategy: `MaxSubtitleCount`;
   - Max chunk subtitles: `1` para o primeiro teste;
   - Configure custos como zero se não houver custo para o provider local.

   O adapter Ollama espera que a resposta contenha JSON com `index` e `text`; confira os detalhes/tentativas do job se o modelo não respeitar o formato.

5. Acesse **Pipelines → Create**, crie um pipeline habilitado. Em **Steps**, adicione um step para o provider/model configurado e deixe-o habilitado.

6. Acesse **Settings** e selecione esse pipeline como padrão e o prompt padrão habilitado como versão de prompt. O startup **não cria um pipeline padrão**.

## 4. Testar descoberta, tradução e saída SRT

1. Crie `C:\Temp\SubtitleTranslator\Input\manual-test.srt` com este conteúdo:

   ```text
   1
   00:00:01,000 --> 00:00:03,000
   Hello, how are you?

   2
   00:00:04,000 --> 00:00:06,000
   I am fine, thank you.
   ```

2. Aguarde o próximo ciclo do scanner (até 10 segundos) e o atraso de estabilidade (5 segundos). Em **Jobs**, o arquivo deve aparecer como descoberto/preparando e depois em processamento.

3. Aguarde o Ollama concluir. O job deve chegar a `Completed`; deve surgir `manual-test.pt-BR.srt` ao lado do original.

4. Confira no arquivo de saída:
   - há duas legendas, na mesma ordem e com os mesmos índices;
   - timestamps permanecem idênticos;
   - texto foi traduzido para pt-BR;
   - o arquivo usa a saída SRT válida.

5. Abra **Details** do job. Verifique chunks, tentativas, provider/model, tokens, latência, custo e timeline.

## 5. Testar scanner e proteção contra duplicidade

1. Confirme que o `.pt-BR.srt` criado não gera outro job.
2. Deixe o SRT original na pasta e aguarde um novo ciclo: o scanner não deve criar job duplicado para o mesmo caminho.
3. Copie outro `.srt` para a pasta e confirme que ele aparece em **Jobs** depois do ciclo do scanner.
4. Para verificar o atraso de estabilidade, copie um arquivo grande ou mantenha o arquivo sendo gravado. O worker só deve reivindicá-lo após o tempo configurado sem nova alteração observada.

O scanner ignora sufixos `.pt-BR.srt` e `.pt.srt` e não cria jobs duplicados para o mesmo caminho. Se a saída de um job concluído for apagada e o arquivo de origem continuar inalterado, o scanner reabre o job e o worker tenta restaurar a saída usando os chunks persistidos, sem repetir chamadas ao provider. Jobs falhos/cancelados continuam exigindo retry manual.

## 6. Testar fallback entre steps

Faça este teste com arquivos descartáveis e um provider local para evitar custos:

1. Duplique o pipeline e configure dois steps habilitados, na ordem desejada.
2. No primeiro step, selecione um model que falhe de forma previsível (por exemplo, um nome inexistente no Ollama). No segundo, mantenha o model que funcionou no teste anterior.
3. Se quiser acelerar, defina no primeiro model `ConfigurationJson` com `pipelineMaxAttempts` igual a `1` e `pipelineRetryDelayMilliseconds` igual a `0`.
4. Selecione o pipeline de fallback em **Settings** e coloque um novo SRT, com outro nome, na pasta monitorada.
5. Esperado: a primeira tentativa aparece com falha; as legendas ainda sem tradução avançam ao segundo step; o job conclui se o segundo provider funcionar.
6. Confira no detalhe do job que tentativas, chunks e errors correspondem à execução.

A UI não inclui simulador de falhas por chunk. Portanto, testar uma falha parcial (alguns chunks aceitos e outros rejeitados pelo mesmo provider) requer controlar o provider externo ou usar os testes automatizados.

## 7. Testar falha e retry manual

1. Faça um job falhar intencionalmente, por exemplo, usando um model Ollama inexistente com uma tentativa.
2. Em **Jobs → Details**, confirme status `Failed`, erro e tentativas registradas.
3. Corrija o model/provider mantendo o mesmo registro de pipeline/model; confirme que o arquivo de origem ainda existe.
4. Clique em **Retry job**.
5. Esperado: o job volta à fila (`Discovered`), limpa o erro atual, mantém o histórico de tentativas e preserva traduções de chunks já concluídos.

Retry fica disponível apenas para jobs `Failed` ou `Cancelled`, com arquivo-fonte existente e pipeline/prompt fixados no job.

## 8. Testar credenciais e providers de nuvem (opcional)

- **OpenRouter:** cadastre provider/model e cole a chave no campo **Credential**; ela é protegida antes de ser salva. Também é possível usar **Advanced → Credential ID** para manter uma referência antiga de variável de ambiente. Execute um SRT pequeno.
- **Azure OpenAI síncrono (API v1):** configure `BaseUrl` com o endpoint terminado em `/openai/v1` (também é aceito `/openai/v1/responses`), autenticação `ApiKey` ou `AzureKeyCredential`, cole a chave em **Credential** e informe `DeploymentName` exatamente igual ao deployment do Azure. Para API v1, deixe `ApiVersion` vazio. O adapter usa Chat Completions em `/openai/v1/chat/completions`.
- **Azure OpenAI legado:** use a URL-base do recurso, informe `ApiVersion` e `DeploymentName`; o adapter monta `/openai/deployments/{deployment}/chat/completions?api-version=...`.
- **Azure Batch:** habilite `SupportsBatch` para um model Azure e use credenciais/deployment válidos. Submissão, polling e inferência podem gerar custos e levar mais tempo; monitore Batch ID/status na página de detalhes. Não há emulador Batch selecionável pela UI.

Para testar uma variável no PowerShell sem armazenar a chave no repositório, defina-a apenas na sessão antes de iniciar o processo (substitua o nome pela referência configurada):

```powershell
$env:OPENROUTER_API_KEY = '<defina localmente a chave>'
```

Não cole chaves na conversa ou em logs compartilhados.

## 9. Health, métricas e autenticação

1. Em Development, acesse `/health`, `/health/live` e `/health/ready` conforme a seção 2.
2. Se `Metrics__AccessToken` não estiver definido, `/metrics` fica aberto em Development:

   ```powershell
   Invoke-WebRequest http://localhost:5043/metrics
   ```

3. Para testar `/metrics` protegido, pare o app, defina `Metrics__AccessToken` no processo, reinicie e faça uma chamada sem Bearer (esperado: 401) e outra com `Authorization: Bearer <token>` (esperado: 200).
4. Para testar login do dashboard em Development, defina `Dashboard__AccessToken` e reinicie. Acesse `/Login`, autentique e confirme que as páginas exigem o cookie. O rate limiter rejeita a sexta tentativa de login dentro da janela de um minuto por IP confiável.

## 10. Recovery e encerramento

1. Para testar o encerramento normal, inicie um job e pressione `Ctrl+C`. O cancellation token pode fazer o job terminar como `Cancelled`; use **Retry job** depois de reiniciar.
2. Para exercitar recovery de processo realmente interrompido, use somente um banco e arquivos descartáveis. O processo interrompido pode deixar estado em andamento para a rotina de recuperação; confirme `/Jobs` e os detalhes após o restart.
3. A recuperação de Batch real precisa de acesso ao Azure. Para cobertura repetível de falhas/restart, rode também os testes automatizados de `TranslationRecoveryServiceTests` e `TranslationPipelineExecutorTests`.
4. Pare a aplicação com `Ctrl+C` e remova a pasta temporária e o banco de teste quando terminar, se não quiser manter os dados.

## 11. O que não é coberto por este teste manual

- O probe de health não executa uma tradução; ele só verifica endpoint/autenticação (e models instalados no Ollama).
- Sem provider funcional, é possível testar inicialização, migrations, páginas, health, scanner e fila, mas não concluir uma tradução.
- A execução real do unit file systemd precisa ser validada em Linux. A suíte automatizada usa providers/handlers simulados e não substitui testes com serviços externos.
