# Revisão de código para publicação

**Data:** 2026-10-03  
**Escopo:** API, worker, portal, MongoDB/PostgreSQL, SQS/RabbitMQ e S3.  
**Resultado:** código validado para implantação de teste em uma task por serviço. A abertura ao tráfego real depende dos itens de infraestrutura abaixo.

## Correções aplicadas

| Prioridade | Achado | Correção |
| --- | --- | --- |
| Alta | A API podia receber chamadas antes de preparar tabelas e índices. | O preparo do banco acontece antes de a API iniciar; `/health/ready` consulta o banco com prazo de 3 segundos. |
| Alta | Um webhook podia trocar o DNS para endereço privado entre validação e conexão. | A conexão HTTP resolve e valida todos os IPs novamente e se conecta diretamente a eles; redirects e proxy foram desativados para webhooks. |
| Alta | Uma queda do worker depois do upload podia deixar objeto S3 sem referência. | A chave é determinística e gravada quando o worker assume o trabalho. A rotina horária remove objetos associados a trabalhos com falha e repete a exclusão se ela falhar. |
| Média | Webhooks lentos podiam atrasar publicação na fila e limpeza. | Dispatch, notificações e limpezas horária/diária agora têm loops independentes; notificações usam até quatro envios em paralelo. |
| Média | Consultas de limpeza limitadas a 100 trabalhos podiam deixar backlog diário. | As rotinas drenam páginas sucessivas até terminar e repetem em 10 segundos quando ocorre falha. |
| Média | O portal não limitava tentativas de login nem validava tokens antifalsificação. | Limite de 10 POSTs por minuto; login e logout exigem token. Respostas do portal e da API com dados do trabalho usam `Cache-Control: no-store`. |
| Média | Faltava validar configurações essenciais antes de uso. | A API valida `PublicBaseUrl`, resolve a fila na inicialização e rejeita solicitações com e-mail quando o Resend não está configurado. |

## Verificação executada

- `dotnet build GeradorExcel.slnx -c Release`: sem erros ou avisos.
- `dotnet format whitespace GeradorExcel.slnx --verify-no-changes`: formatação aprovada.
- `dotnet test GeradorExcel.slnx -c Release`: 12 testes aprovados.
- Imagens Docker de API, worker e portal construídas.
- Teste de ponta a ponta com MongoDB/SQS e PostgreSQL/RabbitMQ: XLSX, CSV, JSON, 800 registros em lotes paralelos e divergência de totais.
- Teste do portal: login, rejeição sem token e logout protegido; limite de tentativas respondeu `429`.
- Teste de recuperação: objetos S3 órfãos de trabalhos com falha foram removidos e a referência foi limpa nos dois bancos.
- Auditoria de pacotes transitivos: nenhuma vulnerabilidade reportada pelas fontes NuGet consultadas.

## Itens para a implantação

1. Configurar HTTPS, segredos, IAM e acesso privado ao banco e ao broker. Restringir o portal ao administrador.
2. Aplicar ciclo de vida S3 para abortar uploads multipart incompletos após um dia; uma queda abrupta pode deixar partes sem objeto final.
3. Configurar DLQ/política de mensagens mortas e alertas para fila, falhas, capacidade do banco e espaço temporário da task.
4. Medir o fluxo completo com dados reais de até 200 MB e um milhão de linhas no tamanho de task ECS escolhido. O benchmark existente cobre apenas o escritor XLSX isolado.
5. Se o portal ganhar réplicas, persistir e compartilhar as chaves ASP.NET Data Protection. Na V1 há uma task.

## Limites conhecidos da V1

- Lotes não têm chave idempotente obrigatória nem validação de colunas entre lotes.
- Falhas de geração não são reprocessadas automaticamente. Uma queda entre etapas da gravação MongoDB encerra o trabalho pelo verificador horário.
- Notificações não têm retry funcional; uma queda entre o envio e a marcação no banco pode produzir notificação duplicada. O receptor do webhook deve aceitar duplicatas pelo `id` do arquivo.
- A criação automática do esquema PostgreSQL é suficiente para esta versão inicial; alterações futuras exigirão migrações versionadas.
