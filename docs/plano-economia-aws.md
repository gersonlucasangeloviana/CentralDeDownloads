# Plano de economia AWS — CentralDeDownloads

**Data da análise:** 4 de outubro de 2026
**Objetivo:** manter a demonstração disponível apenas durante testes e gravação do vídeo, com o menor custo possível nos demais períodos.
**Estado desta análise:** recomendações somente; nenhuma alteração na AWS ou no Terraform foi aplicada.

## Resumo para decisão

1. **Depois da gravação, a maior economia vem de desativar a fase da aplicação (`deploy_app = false`) por Terraform.** Isso remove as três tasks Fargate e o ALB. Permanecem recursos da base, como S3, ECR, SQS, segredos e estado Terraform, com possíveis cobranças próprias. A URL pública deixa de funcionar e o DNS precisará ser conferido na próxima ativação.
2. **Durante uma fase de testes espaçada, o worker é o melhor candidato a scale to zero.** Pode acordar quando houver mensagens no SQS. Antes disso, é preciso corrigir o comportamento de interrupção de trabalhos em andamento e definir uma política de escala segura.
3. **API e portal podem ficar em zero por agenda ou comando manual, mas não acordam automaticamente com uma requisição HTTP nesta arquitetura.** Com zero targets, o ALB continua cobrando e as URLs não atendem. Para a demonstração, agendar ou ligar os serviços pouco antes de usar é mais simples que redesenhar a entrada HTTP.
4. **Configurar alertas de orçamento é prioritário para uma conta pessoal.** Alertas avisam sobre gasto real ou previsto; não impõem um teto de cobrança. [AWS Budgets](https://aws.amazon.com/aws-cost-management/aws-budgets/pricing/).

## O que foi encontrado no projeto

O Terraform em [`infra/aws/web_ecs.tf`](../infra/aws/web_ecs.tf) define três serviços ECS Fargate com `desired_count = 1`: API (0,5 vCPU/1 GiB), worker (1 vCPU/2 GiB e 30 GiB de armazenamento temporário) e portal (0,25 vCPU/0,5 GiB). As três tasks usam ARM64 por padrão e recebem IPv4 público. API e portal ficam atrás de um único ALB em duas zonas. O cluster tem Container Insights ativado. Não há NAT Gateway. A variável `deploy_app` controla, em conjunto, ALB e os três serviços; atualmente não existe variável para ligar ou desligar cada serviço separadamente. A configuração local de implantação indica `deploy_app = true`, mas **não foi possível confirmar o estado real da conta**.

Também já existem medidas úteis: SQS com long polling de 20 segundos, S3 com expiração dos arquivos após dois dias e aborto de uploads incompletos após um dia, e retenção de logs de 30 dias. O ECR usa tags imutáveis, sem política de limpeza de imagens. O bucket de estado Terraform tem versionamento, sem expiração de versões antigas. Esses detalhes aparecem em [`infra/aws/storage_queue.tf`](../infra/aws/storage_queue.tf), [`infra/aws/registry_logs.tf`](../infra/aws/registry_logs.tf) e [`infra/aws/state/main.tf`](../infra/aws/state/main.tf).

O MongoDB Atlas, Cloudflare e Resend são serviços externos à AWS. Seus custos e limites precisam ser conferidos separadamente.

## Estimativa da parte contínua

Exemplo para **720 horas (30 dias)** em `us-east-1`, com uma task de cada serviço 24 horas por dia, ARM64, sem descontos, créditos, impostos ou variação de uso. São valores de referência, **não uma fatura**. A conta Fargate usa os preços por segundo do exemplo oficial da [AWS Fargate](https://aws.amazon.com/fargate/pricing/); o ALB usa apenas sua tarifa horária base da [página de preços do ELB](https://aws.amazon.com/elasticloadbalancing/pricing/); o IPv4 usa US$ 0,005 por endereço/hora da [página da VPC](https://aws.amazon.com/vpc/pricing/).

| Parcela | Estimativa em 30 dias | Observação |
| --- | ---: | --- |
| Fargate API | US$ 14,22 | 0,5 vCPU + 1 GiB |
| Fargate worker | US$ 29,24 | 1 vCPU + 2 GiB + 10 GiB temporários acima dos 20 GiB incluídos |
| Fargate portal | US$ 7,11 | 0,25 vCPU + 0,5 GiB |
| ALB, tarifa horária | US$ 16,20 | LCUs cobradas à parte |
| IPv4 público das três tasks | US$ 10,80 | Um IP por task enquanto executa |
| IPv4 público do ALB | cerca de US$ 7,20 | Hipótese de dois endereços, um por zona; conferir no Cost Explorer |
| Quatro segredos no Secrets Manager | cerca de US$ 1,60 | US$ 0,40 por segredo/mês; chamadas e segredo opcional do Resend à parte. [Preço](https://aws.amazon.com/secrets-manager/pricing/) |
| **Subtotal ilustrativo** | **cerca de US$ 86,37/mês** | Sem LCUs, tráfego, logs, métricas, ECR, S3, SQS e outros recursos |

Esse subtotal não considera créditos ou faixa gratuita da conta. Para uma comparação de ordem de grandeza, deixar **só o worker em zero** reduz a estimativa em cerca de **US$ 32,84/mês** (task + seu IPv4), desde que ele não execute nenhuma hora. Deixar **as três tasks em zero com o ALB ainda criado** mantém aproximadamente **US$ 25/mês** de ALB base, dois IPv4 estimados e quatro segredos, além dos demais serviços. Desativar `deploy_app` elimina também a parcela do ALB, mas não os custos da base. A AWS cobra [LCUs pelo uso do ALB](https://aws.amazon.com/elasticloadbalancing/pricing/) e [IPv4 públicos do balanceador](https://aws.amazon.com/elasticloadbalancing/pricing/) separadamente.

## Possibilidades, em ordem prática

| Opção | Quando faz sentido | Economia e cuidado principal |
| --- | --- | --- |
| **Desligar a fase da aplicação após o vídeo** | Sem necessidade de acesso público até o próximo teste | Maior redução de custo contínuo. Planejar o retorno do ALB, DNS e conexão com Atlas antes da próxima gravação. |
| **Worker de 0 a 1 task conforme SQS** | API permanece disponível para receber trabalhos | Economiza o worker ocioso. O início tem atraso de métrica e inicialização; proteger trabalhos ativos antes de reduzir a capacidade. |
| **API e portal por janela de uso** | Testes e gravações em horários conhecidos | Economiza duas tasks e seus IPs fora da janela. O ALB permanece cobrado e as URLs ficam indisponíveis enquanto as tasks estão em zero. |
| **Limpar imagens antigas no ECR** | Publicações frequentes com tags imutáveis | Evita crescimento de armazenamento; manter a imagem implantada e uma versão de retorno. Usar prévia da política antes de excluir. [Políticas ECR](https://docs.aws.amazon.com/AmazonECR/latest/userguide/lp_creation.html). |
| **Rever Container Insights e volume de logs** | A fatura mostrar CloudWatch relevante | Container Insights pode gerar cobranças adicionais; manter a observabilidade necessária para testes e medir antes de desligar métricas. [Preços CloudWatch](https://aws.amazon.com/cloudwatch/pricing/). |
| **Rever tamanho das tasks e Spot** | Depois de medir CPU, memória, duração e tolerância a interrupção | API/portal podem talvez usar menos recursos, dentro dos tamanhos válidos. Fargate Spot oferece desconto variável, mas interrupções podem afetar trabalhos; não usar no worker atual sem tratar retomada. [Preços Fargate](https://aws.amazon.com/fargate/pricing/). |
| **Rever arquitetura para uso ocasional de longo prazo** | Se o projeto continuar após o vídeo | Avaliar uma entrada sob demanda e execução por tarefa para processamento. Exige redesenho, testes e nova comparação de custos; não é ajuste simples do ECS atual. |

### Condições para scale to zero seguro

- **Worker:** ECS Service Auto Scaling permite capacidade mínima zero e pode escalar a partir de uma métrica de demanda; a AWS documenta escala baseada em [SQS](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-autoscaling-queue.html) e o comportamento de [capacidade mínima zero](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-auto-scaling.html). Para este projeto, uma política de 0 a 1 task, com alarme para fila visível e redução apenas quando não houver mensagens visíveis nem em processamento, é um ponto de partida. Não basta usar CPU: sem task, não há métrica de CPU que acorde o serviço. A latência de partida precisa caber no prazo de 30 minutos do trabalho.
- **Risco concreto no código atual:** em [`src/CentralDeDownloads.Worker/Worker.cs`](../src/CentralDeDownloads.Worker/Worker.cs), uma interrupção durante `ProcessAsync` marca o trabalho como falho e remove os lotes. Em [`src/CentralDeDownloads.Core/SqsJobQueue.cs`](../src/CentralDeDownloads.Core/SqsJobQueue.cs), `ReleaseAsync` não devolve a mensagem antecipadamente à fila. Portanto, antes da escala automática ou do Spot, é preciso definir tratamento de encerramento, visibilidade da mensagem e [proteção da task durante o processamento](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-scale-in-protection.html). Caso contrário, reduzir a capacidade pode perder um arquivo em andamento.
- **API e portal:** ECS aceita `desired_count = 0`, mas o ALB não inicia tasks por uma chamada HTTP. O caminho adequado para o uso temporário atual é iniciar antecipadamente e esperar os health checks. O portal depende da API; ligar apenas o portal não basta. A [escala agendada do ECS](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-autoscaling-schedulescaling.html) é uma opção se as janelas forem previsíveis.
- **Terraform:** editar `desired_count` diretamente no console ou CLI enquanto o arquivo mantiver `desired_count = 1` cria divergência. Um próximo `terraform apply` pode voltar a ligar as tasks. A implementação futura deve representar o modo econômico no Terraform ou registrar explicitamente como a escala automática controla esse campo.
- **Limpeza e dados:** a limpeza diária de arquivos e histórico roda dentro da API em [`src/CentralDeDownloads.Api/FileCleanupService.cs`](../src/CentralDeDownloads.Api/FileCleanupService.cs). Com a API desligada, essa rotina não roda; o S3 ainda expira os arquivos após dois dias, mas o histórico no Atlas pode ficar pendente até a API voltar. Mensagens no SQS podem aguardar até 14 dias pela configuração atual, mas trabalhos que ultrapassarem 30 minutos podem falhar pelo prazo da aplicação. Não deixar requisições pendentes ao encerrar a demonstração.

## Sequência sugerida quando chegar a hora de agir

1. **Antes do próximo teste:** consultar Billing/Cost Explorer por serviço, região e tipo de uso; conferir `Fargate`, `ElasticLoadBalancing`, `PublicIPv4`, `CloudWatch`, `SecretsManager`, `ECR`, `S3`, `SQS` e transferências. Conferir também a cobrança do Atlas. A sessão AWS disponível nesta análise estava expirada, então não houve leitura da fatura.
2. **Definir um orçamento mensal compatível com o limite pessoal**, com avisos em patamares antecipados e previsão de ultrapassagem. Os avisos padrão do [AWS Budgets](https://aws.amazon.com/aws-cost-management/aws-budgets/pricing/) podem ser configurados sem custo, mas não desligam recursos automaticamente.
3. **Para gravar:** manter API, worker e portal ativos, verificar health checks e conclusão dos trabalhos, baixar os arquivos necessários e deixar a fila vazia.
4. **Depois de gravar:** quando decidir desligar a demonstração, alterar `deploy_app` para `false`, revisar cuidadosamente o `terraform plan` e então aplicar. Esse passo é futuro; **não foi executado neste trabalho**. Conferir se os CNAMEs da Cloudflare ainda apontam para o ALB removido e registrar o procedimento de reativação.
5. **Na próxima rodada de otimização:** implementar e testar primeiro o encerramento seguro do worker; depois configurar escala SQS de 0 a 1, medir atraso de partida e custos reais, e só então avaliar Spot ou redução do tamanho das tasks.

**Referências principais:** [Fargate](https://aws.amazon.com/fargate/pricing/), [ECS Service Auto Scaling](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-auto-scaling.html), [ECS com SQS](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-autoscaling-queue.html), [ELB](https://aws.amazon.com/elasticloadbalancing/pricing/), [IPv4 público](https://aws.amazon.com/vpc/pricing/), [Secrets Manager](https://aws.amazon.com/secrets-manager/pricing/) e [AWS Budgets](https://aws.amazon.com/aws-cost-management/aws-budgets/pricing/).
