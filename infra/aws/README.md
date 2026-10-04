# AWS com Terraform — CentralDeDownloads

Esta configuração cria S3 privado, SQS com DLQ, ECR, VPC com duas sub-redes públicas em zonas de disponibilidade diferentes, papéis IAM, nomes de segredos, CloudWatch, certificado ACM e um cluster ECS. Na segunda fase, cria ALB e uma task Fargate para cada um dos três serviços. As tasks recebem IP público e acessam a internet pelo Internet Gateway; não há NAT Gateway. O ALB encaminha as requisições da API e do portal às tasks. O DNS de `vianadev.com.br` permanece na Cloudflare. O MongoDB Atlas continua fora da AWS; sua conexão é colocada no Secrets Manager **fora do Terraform** para não gravar a senha no state.

**Nomes da implantação existente:** o prefixo `gerador-excel`, o banco `gerador_excel`, o bucket de estado e a chave do backend foram mantidos para que este código continue compatível com os recursos já criados. A identidade do código e dos projetos é `CentralDeDownloads`. Mudar `name_prefix`, o nome do bucket ou a chave do backend em um ambiente existente exige um plano de migração do estado e dos recursos; não faça essa troca como simples renomeação.

## Pré-requisitos

- Conta AWS e permissões para VPC, S3, SQS, ECR, IAM, Secrets Manager, CloudWatch, ECS, ACM e ELB.
- AWS CLI autenticada, Terraform >= 1.10, Docker com Buildx e Python 3.
- Região AWS `us-east-1` e acesso para editar os registros DNS de `vianadev.com.br` na Cloudflare.
- Revisar custos: não há cobrança de NAT Gateway. A segunda fase cobra ALB, três tasks Fargate e os endereços IPv4 públicos das tasks; ECR, S3, SQS, CloudWatch e outros recursos também podem gerar custos de uso.

## 0. Autenticar o Terraform na AWS

Use credenciais temporárias de uma pessoa autorizada a criar S3, IAM, VPC, ECS e os demais recursos deste projeto. Para a primeira implantação, o conjunto de permissões `AdministratorAccess` no IAM Identity Center é uma forma simples de iniciar; depois, reduza as permissões. Habilite o IAM Identity Center no console AWS, crie seu usuário e atribua esse conjunto de permissões à conta. Ative MFA para o usuário. Não crie access key do usuário root nem coloque chaves AWS no repositório.

No terminal, configure um perfil separado para este projeto:

```sh
aws configure sso --profile central-downloads
aws sso login --profile central-downloads
aws sts get-caller-identity --profile central-downloads
export AWS_PROFILE=central-downloads
```

No assistente `aws configure sso`, informe a URL de início do IAM Identity Center, a região em que ele foi habilitado (**`us-east-2`, Ohio, nesta conta**), a conta e o conjunto de permissões; use **`us-east-1`, N. Virginia, como região padrão da CLI** para os recursos do app. O Identity Center pode estar em uma região diferente da infraestrutura. Confirme que o `Account` mostrado por `get-caller-identity` é a conta AWS desejada **antes** de executar `terraform apply`. O perfil fica em `~/.aws/config`, fora do projeto. Ao expirar a sessão, repita `aws sso login --profile central-downloads`. Mantenha `AWS_PROFILE=central-downloads` no mesmo terminal dos comandos Terraform e do script de envio das imagens. Se já usa o perfil `gerador-excel`, pode continuar com ele; o nome do perfil não altera recursos AWS.

## 1. Criar o bucket de estado

O estado é separado dos arquivos gerados pelo app. Aplique uma vez por ambiente:

```sh
cd infra/aws/state
terraform init
terraform apply -var='aws_region=us-east-1' -var='environment=prod'
terraform output -raw bucket_name
```

O estado deste pequeno bootstrap permanece local; guarde seu `terraform.tfstate` em local seguro. O bucket tem versionamento, criptografia e bloqueio de acesso público. O recurso tem `prevent_destroy`.

## 2. Criar a base do app

Em `infra/aws`, copie `terraform.tfvars.example` para `terraform.tfvars` e `backend.hcl.example` para `backend.hcl`. Substitua região e nome do bucket de estado. `terraform.tfvars` e `backend.hcl` são ignorados pelo Git.

```sh
cd infra/aws
terraform init -backend-config=backend.hcl
terraform plan -out=plano.tfplan
terraform apply plano.tfplan
terraform output
```

Neste primeiro apply, mantenha `deploy_app = false`. O Terraform cria os recursos de base e solicita um certificado ACM para `apiga.vianadev.com.br` e `ga.vianadev.com.br`; não inicia os contêineres. Os nomes dos segredos aparecem no output, mas os **valores** ainda precisam ser informados no console do Secrets Manager:

Se quiser usar os recursos AWS também no teste local, execute `./sync-local-config.sh` em `infra/aws` após o apply. Ele preenche `Aws__Region`, `Aws__Bucket` e `Aws__QueueUrl` no `.env.local` existente sem alterar a conexão MongoDB.

| Segredo | Valor a inserir |
| --- | --- |
| `mongo-connection` | URI Atlas completa, igual à `Mongo__ConnectionString` local |
| `api-key` | Chave aleatória com pelo menos 16 caracteres |
| `download-signing-key` | Chave aleatória com pelo menos 32 caracteres |
| `portal-password` | Senha do administrador com pelo menos 12 caracteres |

O mesmo segredo `api-key` é usado por API e portal. Não coloque esses valores em `terraform.tfvars`, comandos de shell, state ou Git. Se a senha do Atlas for trocada, atualize o segredo antes de substituir as tasks. As tasks recebem segredos somente ao iniciar: após uma rotação, faça novo deploy.

Para enviar notificações por e-mail, crie no Secrets Manager um segredo com a chave do Resend, informe seu ARN em `resend_secret_arn` e configure `resend_from` com um remetente de domínio verificado no Resend. A API envia por e-mail um link temporário de download quando o arquivo fica pronto; o arquivo gerado não é anexado automaticamente. Depois de mudar esses valores, aplique o Terraform para iniciar uma nova task da API. Para usar a marca atual, configure `Central de Downloads <arquivos@vianadev.com.br>` como remetente; uma implantação anterior pode continuar exibindo o nome antigo até receber nova configuração.

**Acesso ao Atlas:** sem NAT, cada task tem um IP público de saída que pode mudar a cada inicialização ou substituição. Portanto, não existe um único IP `/32` para cadastrar permanentemente na lista de acesso do Atlas. Para um teste curto com conexão pública, uma opção é permitir temporariamente `0.0.0.0/0` em **Network Access** do Atlas, com usuário de banco exclusivo, senha forte e revogação desse acesso ao fim do teste. Isso permite tentativas de conexão de qualquer origem e aumenta a exposição do banco. Para manter a lista restrita, será necessário definir outra forma de saída com IP fixo ou uma conexão privada com o Atlas, sujeitas a custos e configuração adicionais. Confira também que `vpc_cidr` não se sobrepõe à rede do Atlas se optar por conexão privada no futuro.

## 3. Validar o certificado na Cloudflare

Execute `terraform output -json acm_validation_records`. Para cada um dos dois domínios, crie na Cloudflare o CNAME com `name` e `target` informados. Defina **DNS only** (nuvem cinza), sem CNAME flattening. Confira o nome final do registro na interface: ela pode acrescentar `vianadev.com.br` automaticamente. Mantenha esses CNAMEs para a renovação automática do certificado. Aguarde o certificado ficar **Issued** no ACM antes de ativar o app.

## 4. Publicar as imagens

Após a base estar pronta, a partir de `infra/aws`:

```sh
chmod +x push-images.sh
./push-images.sh 2026-10-03-01
```

O script constrói e envia as três imagens para os repositórios ECR com a mesma tag. A arquitetura de build é `ARM64` por padrão; `cpu_architecture` em `terraform.tfvars` pode ser `X86_64`. Use uma tag nova em cada publicação; os repositórios não permitem sobrescrever tags.

## 5. Ativar HTTPS e ECS

Depois de validar o certificado, preencher os segredos, definir a política de acesso de rede no Atlas e publicar as imagens, configure no `terraform.tfvars`:

```hcl
deploy_app           = true
portal_allowed_cidrs = ["SEU_IP_PUBLICO/32"]
image_tag            = "2026-10-03-01"
```

O Terraform cria o ALB e inicia uma task de cada serviço nas sub-redes públicas. O ALB registra os IPs privados das tasks como destinos. Os security groups permitem entrada na API e no portal somente a partir do ALB; o worker não tem regra de entrada. Os IPs públicos das tasks servem para conexões de saída com Atlas, ECR, Secrets Manager, SQS e S3. A API atende pela rota `/health/ready`; o portal usa `/health` no health check. O acesso ao portal pelo listener HTTPS fica limitado aos CIDRs informados. O portal chama a API por `https://apiga.vianadev.com.br`.

```sh
terraform plan -out=plano.tfplan
terraform apply plano.tfplan
terraform output api_url
terraform output portal_url
terraform output -raw alb_dns_name
```

Com o endereço de `alb_dns_name`, crie na Cloudflare dois CNAMEs: `apiga` → DNS do ALB e `ga` → DNS do mesmo ALB. Use **DNS only** inicialmente. Mantenha `ga` em DNS only enquanto o controle de IP do portal estiver no ALB; se ligar o proxy da Cloudflare, o ALB verá os IPs da Cloudflare e a regra de IP do administrador deixará de funcionar como planejado. Depois que o DNS propagar, teste as duas URLs. Ao publicar uma nova versão, envie as imagens com outra tag, atualize `image_tag` e aplique novamente.

Com `Swagger__Enabled=true` na task da API, a documentação interativa fica em `https://apiga.vianadev.com.br/swagger/`. Para testar rotas protegidas, use o botão **Authorize** e a chave configurada em `Security__ApiKey`.

## Verificação e operação

- Confirme que os três serviços ECS estão estáveis e que os dois target groups do ALB estão saudáveis.
- Execute o fluxo de `tests/smoke.py` com a chave da API, depois teste login e download no portal.
- Verifique os logs em `/ecs/<nome>/api`, `/worker` e `/portal` e os alarmes de idade da fila e DLQ. Os alarmes ainda não têm canal de notificação; configure um destino antes do tráfego real.
- Teste arquivo grande com dados representativos. O worker foi inicialmente configurado com 1 vCPU, 2 GiB de memória e 30 GiB de disco temporário; ajuste após medir o fluxo real.
- A regra S3 expira objetos em `arquivos/` após dois dias como salvaguarda. O app continua controlando a disponibilidade de 24 horas e sua limpeza própria.

**Revise o plano e os custos da conta antes de cada `terraform apply`.** O Terraform não cria o cluster MongoDB Atlas nem altera sua lista de acesso. Revogue qualquer acesso temporário amplo ao Atlas ao encerrar o teste.
