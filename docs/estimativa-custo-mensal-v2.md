# Estimativa mensal da V2 — 4 de outubro de 2026

**Moeda:** USD. **Cenário:** região AWS `us-east-1`, 30 dias (720 h), API/worker/portal ligados 24 h por dia, uma task ARM64 de cada serviço, um ALB, sem descontos, créditos ou impostos. É uma estimativa de preços públicos, não a fatura. O Terraform e o ECS confirmam essa configuração; o Cost Explorer ainda não contém um dia completo da nova implantação.

## Parcela contínua da AWS

| Recurso | Cálculo | USD/mês |
| --- | --- | ---: |
| Fargate API | 0,5 vCPU + 1 GiB × 720 h | 14,22 |
| Fargate worker | 1 vCPU + 2 GiB + 10 GiB temporários acima dos 20 GiB incluídos × 720 h | 29,24 |
| Fargate portal | 0,25 vCPU + 0,5 GiB × 720 h | 7,11 |
| ALB | US$ 0,0225/h × 720 h; LCUs à parte | 16,20 |
| IPv4 público das três tasks | 3 × US$ 0,005/h × 720 h | 10,80 |
| IPv4 público do ALB | hipótese de 2 endereços × US$ 0,005/h × 720 h | 7,20 |
| Secrets Manager | 4 segredos × US$ 0,40/mês; chamadas à parte | 1,60 |
| **Base da AWS** | **Serviços sempre ativos** | **86,37** |

As taxas são as dos exemplos oficiais de [Fargate ARM64](https://aws.amazon.com/fargate/pricing/), [ALB](https://aws.amazon.com/elasticloadbalancing/pricing/), [IPv4 público](https://aws.amazon.com/vpc/pricing/) e [Secrets Manager](https://aws.amazon.com/secrets-manager/pricing/). O total de IPv4 do ALB é hipótese, não leitura de fatura. O worker ainda reserva 30 GiB de armazenamento temporário, mesmo que o arquivo final agora vá em partes ao S3; reduzir essa reserva em uma próxima revisão economizaria apenas cerca de **US$ 0,80/mês** nessa task.

## Serviços externos e uso variável

| Recurso | Faixa/cenário público | Situação nesta conta |
| --- | --- | --- |
| MongoDB Atlas | [Flex: US$ 8–30/mês](https://www.mongodb.com/docs/atlas/billing/atlas-flex-costs/), até 5 GB; dedicado começa em cerca de US$ 60/mês | **Plano Flex confirmado pelo usuário.** Os 5 GB de dados e índices não comportam o ensaio de 100 milhões de linhas com 512 caracteres. O valor exato no intervalo depende do uso. |
| Cloudflare | [Plano gratuito disponível](https://www.cloudflare.com/plans/); domínio e recursos pagos podem acrescentar custo | Usado para DNS; plano e renovação do domínio não foram verificados. |
| Resend | [Free: US$ 0 até 3.000 e-mails/mês e 100/dia; Pro: US$ 20/mês](https://resend.com/pricing?product=transactional) | E-mail é opcional na API. O plano e o volume real precisam ser confirmados. |
| S3 | Arquivos expiram após 2 dias; cobrança de armazenamento, partes PUT e downloads | Varia com quantidade, tamanho e quantas vezes o arquivo é baixado. |
| ALB LCU, tráfego, CloudWatch, ECR e SQS | Cobrança por uso; SQS inclui [1 milhão de requisições/mês sem custo](https://aws.amazon.com/sqs/pricing/) | Medir após um mês de uso. Container Insights e logs estão ativos. |

**Número para apresentar com clareza:** **US$ 86,37/mês de base AWS**. Com o **Atlas Flex confirmado**, Cloudflare e Resend nos planos gratuitos e sem extras relevantes, a base conjunta fica em **US$ 94,37–116,37/mês**, **mais** tráfego, ALB LCU, S3, CloudWatch, ECR, eventuais e-mails e tributos. O plano real do Atlas está confirmado, mas seu valor exato no intervalo e os planos Cloudflare/Resend não foram conferidos na fatura; a faixa não é o total final.

### Exemplo de volume: 100 arquivos CSV de 1 milhão por mês

Mantendo o perfil do teste (512 caracteres por linha), cada arquivo medido ocupou **519.888.915 bytes**, ou cerca de **0,52 GB decimais**. Cem arquivos somam **52 GB gerados**. Se forem distribuídos uniformemente no mês e apagados em até 2 dias, o armazenamento médio da saída seria aproximadamente **3,5 GB-mês**; a ordem de grandeza do S3 Standard é **menos de US$ 0,10/mês de armazenamento** à tarifa de [US$ 0,023/GB-mês em `us-east-1`](https://aws.amazon.com/s3/pricing/), mais requisições. Baixar cada arquivo uma vez enviaria outros **52 GB para a Internet**; a API também envia os lotes ao Atlas por rede pública. A AWS fornece **100 GB/mês de transferência de saída para Internet sem custo**, compartilhados entre serviços e regiões elegíveis, depois aplica tarifa por GB ([preços de transferência AWS](https://aws.amazon.com/ec2/pricing/on-demand/)). Por isso, a parcela variável depende de downloads, outras cargas da conta e localização/plano do Atlas; não há um valor único honesto sem volume real.

O tamanho dos lotes de 1.000 reduz chamadas HTTP: **100 mil lotes para 100 arquivos de 1 milhão**, contra **1 milhão** na V1 com 100 itens. Isso reduz tempo de ingestão e pressão de requisições, mas a maior parte da base mensal acima continua cobrada enquanto as tasks e o ALB permanecem ligados.

## Como fechar o valor real

1. Conferir o custo mensal efetivo do cluster Flex no painel de faturamento do Atlas; o plano não é criado pelo Terraform AWS.
2. Obter no AWS Cost Explorer um mês completo por `SERVICE` e `USAGE_TYPE`, incluindo ECS/Fargate, ELB/LCU, VPC IPv4, transferência, S3, SQS, CloudWatch, ECR e Secrets Manager.
3. Informar arquivos/mês, tamanho médio, quantidade de downloads, e-mails/dia e plano do Cloudflare/Resend; substituir o cenário ilustrativo pelos volumes reais.

O [plano de economia](plano-economia-aws.md) mostra o efeito de desligar a aplicação fora das janelas de teste. Nenhuma escala para zero foi aplicada nesta atualização.
