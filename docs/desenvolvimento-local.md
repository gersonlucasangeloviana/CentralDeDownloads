# Desenvolvimento local

Este roteiro executa a API, o worker e o portal no computador, com MongoDB, SQS e S3 locais. É necessário ter .NET 10, Docker e Python 3.

## 1. Iniciar as dependências

Na raiz do repositório:

```bash
docker compose -f compose.dev.yaml up -d
```

O Compose também inicia PostgreSQL e RabbitMQ para testar os provedores alternativos. O LocalStack cria o bucket e a fila ao iniciar.

## 2. Configurar os serviços

Crie `.env.local` na raiz com estes valores **somente para desenvolvimento**:

```dotenv
Storage__Provider=MongoDB
Mongo__ConnectionString=mongodb://localhost:27017
Mongo__Database=central_downloads
Queue__Provider=SQS
Aws__Region=us-east-1
Aws__Bucket=central-downloads-local
Aws__QueueUrl=http://localhost:4566/queue/us-east-1/000000000000/central-downloads-local
Aws__ServiceUrl=http://localhost:4566
AWS_ACCESS_KEY_ID=test
AWS_SECRET_ACCESS_KEY=test
Security__ApiKey=chave-publica-apenas-para-teste-local-12345
Security__DownloadSigningKey=segredo-publico-apenas-para-teste-local-1234567890
PublicBaseUrl=http://localhost:5151
Portal__Password=senha-publica-apenas-para-teste-local
Api__BaseUrl=http://localhost:5151
Api__Key=chave-publica-apenas-para-teste-local-12345
```

O arquivo é ignorado pelo Git e pelo Docker. Os valores acima são exemplos públicos para a máquina local; crie segredos próprios para qualquer ambiente acessível pela rede. O .NET não carrega `.env.local` automaticamente.

## 3. Executar API, worker e portal

Abra três terminais na raiz do repositório. Em **cada um**, execute `set -a; source .env.local; set +a` e depois inicie um serviço:

```bash
dotnet run --project src/CentralDeDownloads.Api --launch-profile http
dotnet run --project src/CentralDeDownloads.Worker
dotnet run --project src/CentralDeDownloads.Portal --launch-profile http
```

Cada comando permanece ativo em seu próprio terminal. A API expõe [Swagger](http://localhost:5151/swagger/) e o portal responde em [http://localhost:5192](http://localhost:5192).

## 4. Verificar

Em outro terminal, depois de carregar `.env.local`:

```bash
dotnet test CentralDeDownloads.slnx -c Release
CENTRAL_DOWNLOADS_API_URL=http://localhost:5151 CENTRAL_DOWNLOADS_API_KEY="$Security__ApiKey" python3 tests/smoke.py
CENTRAL_DOWNLOADS_API_URL=http://localhost:5151 python3 tests/swagger_smoke.py
CENTRAL_DOWNLOADS_PORTAL_URL=http://localhost:5192 CENTRAL_DOWNLOADS_PORTAL_PASSWORD="$Portal__Password" python3 tests/portal_smoke.py
```

O teste `smoke.py` cria trabalhos e arquivos de teste. Para executar os fluxos manualmente, consulte o [guia da API](api.md) ou a [coleção Postman](../postman/README.md).

## Provedores alternativos

- **PostgreSQL:** use `Storage__Provider=PostgreSQL` e `Postgres__ConnectionString='Host=localhost;Port=5433;Database=central_downloads;Username=central;Password=central-local'` na API e no worker.
- **RabbitMQ:** use `Queue__Provider=RabbitMQ`, `RabbitMq__Uri=amqp://central:central-local@localhost:5673/` e `RabbitMq__QueueName=central-downloads-local` na API e no worker.

Reinicie API e worker após alterar o provedor. Trocar o banco não migra trabalhos existentes; trocar a fila com trabalhos pendentes exige esvaziar ou reencaminhar a fila anterior.
