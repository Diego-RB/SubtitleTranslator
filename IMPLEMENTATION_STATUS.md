# Estado verificado da implementação

**Verificado em:** 2026-10-20  
**Referências funcionais:** `ARCHITECTURE_SPEC.md` e `GITHUB_COPILOT_MASTER_PROMPT.md`

Este arquivo registra o que foi confirmado no código e separa implementação atual de requisitos ainda pendentes. `PROJECT_DOCUMENTATION.md` contém descrições históricas de funcionalidades planejadas e não deve ser usado como inventário confiável sem conferência no código.

## Implementado

- ASP.NET Core Razor Pages em .NET 10, DI, logging, SQLite/EF Core e migrations; endpoints `/health`, `/health/live` e `/health/ready`.
- Entidades persistidas `Provider`, `ModelDefinition`, `Pipeline`, `PipelineStep`, `TranslationJob`, `TranslationChunk`, `TranslationAttempt`, prompts e configurações do orquestrador.
- Parser, writer, validator e chunker SRT; preservação de índices/timestamps, validação antes de gerar o arquivo e gravação final atômica.
- Adapters HTTP para Ollama, OpenRouter/OpenAI-compatible e Azure OpenAI, além do ciclo Azure Batch. Fallback sequencial preserva legendas concluídas e encaminha as pendentes.
- Scanner, worker, heartbeat por job, recuperação e dashboard com autenticação; `/metrics` tem autenticação separada.
- Dashboard de jobs, detalhes, providers, models, pipelines, prompts e configurações.
- Retry manual de jobs falhos/cancelados na página de detalhes. O histórico e os chunks concluídos são preservados.
- Atraso configurável antes do processamento para evitar ler arquivos ainda sendo copiados: `Scanner:FileStabilityDelaySeconds` (15 segundos por padrão), baseado no horário da última gravação observado.
- O indicador operacional da Home reflete o estado do `SubtitleTranslationWorker` no processo atual (`WORKER ONLINE/OFFLINE`).
- Azure aceita `ApiKey` e `AzureKeyCredential` por variável de ambiente; a configuração exige HTTPS. Managed Identity não é suportada.
- Unit file systemd, regras Prometheus e guia de implantação estão presentes no repositório.

## Correções realizadas nesta revisão

- Corrigido o número gravado no histórico quando uma tentativa síncrona falha e é repetida.
- O cancelamento de Batch tenta cancelar o ID retornado pela submissão mesmo antes de o ID ser persistido no job.
- A configuração Azure não aceita HTTP nem opções de autenticação que o adapter não implementa; a UI mostra apenas opções aceitas.
- Jobs falhos/cancelados podem ser colocados novamente na fila sem apagar tentativas nem traduções já concluídas.
- A Home deixou de declarar a aplicação “online” incondicionalmente; agora reporta o worker.

## Limitações e validações pendentes

- O retry manual cobre estados `Failed` e `Cancelled`; o scanner não cria automaticamente um novo job para um caminho com job terminal.
- O atraso de estabilidade reduz corridas durante cópia, mas não é uma garantia de arquivo imutável (depende dos metadados de última gravação do filesystem).
- `MaxTokens` usa uma estimativa aproximada baseada no tamanho do texto; não inclui prompt/glossário/envelope JSON e não usa tokenizer específico do modelo.
- `WorkerRuntimeStatus` é em memória e descreve a instância atual; não é um registro durável ou heartbeat global no SQLite.
- A suíte usa handlers/providers simulados. Não foram executadas chamadas reais a Ollama, OpenRouter ou Azure, nem um ciclo real de Azure Batch.
- Os testes de persistência usam `EnsureCreated()`; a aplicação de todas as migrations sobre um banco persistente ainda precisa de teste automatizado.
- O serviço systemd e permissões do filesystem ainda precisam ser exercitados em um host Linux.
- Não há Managed Identity Azure nem contas individuais/auditoria de operadores.

## Validação desta revisão

Comando: `dotnet test --no-restore --verbosity minimal`  
Resultado: **70 testes aprovados, 0 falhas** após as correções.
