# Diário de comparação da ingestão

Este arquivo preserva a configuração e os resultados de cada rodada. Os resumos do k6 estão em [`benchmarks/ingestao-2026-10-04-local/`](../benchmarks/ingestao-2026-10-04-local/). A matriz é executável com [`tests/run-ingestion-benchmark.py`](../tests/run-ingestion-benchmark.py). Uma linha por execução fica em `resultados.jsonl` para montar o relatório final.

## Referência AWS — 2026-10-03, versão anterior

API `https://apiga.vianadev.com.br`, CSV, 100.000 linhas, 512 caracteres por linha, 100 itens por lote, 8 lotes simultâneos: envio **32,591 s**, geração **1,925 s**, primeiro POST até GET `pronto` **37,732 s**, saída **51.888.914 bytes**, 0 falhas HTTP. [Relatório original](resultado-formatos-aws-2026-10-03.md). Esta referência não deve ser comparada numericamente com a matriz local para calcular ganho, pois banco, rede e worker são diferentes.

Na mesma campanha AWS, **1 milhão** de registros produziu **519.888.915 bytes em CSV** e **543.888.897 bytes em JSON**. Os arquivos perto de 50 MB eram os de **100 mil** registros. O CSV já é texto simples e foi cerca de 4,4% menor que o JSON nesse perfil. Só a coluna de conteúdo de 512 caracteres por linha soma 512 milhões de caracteres em 1 milhão de linhas; reduzir muito mais o arquivo sem compressão exigiria reduzir os dados de entrada.

## Matriz local — 2026-10-04, implementação nova

Mac ARM, API e worker .NET 10 Release, MongoDB 8 e RabbitMQ 4.2 em Docker, LocalStack 4.12 como S3, uma solicitação CSV por execução, 100.000 registros, 512 caracteres pseudoaleatórios por registro, até 1 MiB por corpo, polling a cada 2 s, parte multipart de 5 MiB para exercitar mais de uma parte. Cada combinação foi executada uma vez; os tempos não são médias nem garantem a mesma ordem de desempenho na AWS. Todas terminaram em `pronto`, com 0 falhas HTTP e saída idêntica de **51.888.914 bytes**.

| Itens/lote | Lotes paralelos | Envio | Geração | Primeiro POST → GET `pronto` | HTTP p95 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 1 | 6,055 s | 0,833 s | 10,073 s | 3,8 ms |
| 100 | 4 | 4,781 s | 0,807 s | 6,791 s | 6,9 ms |
| 100 | 8 | 4,611 s | 0,771 s | 8,624 s | 9,9 ms |
| 100 | 16 | 4,382 s | 0,782 s | 6,390 s | 13,6 ms |
| 100 | 32 | 4,389 s | 0,842 s | 6,408 s | 25,2 ms |
| 1.000 | 1 | 4,874 s | 0,864 s | 6,886 s | 16,4 ms |
| 1.000 | 4 | 4,159 s | 0,844 s | 8,171 s | 22,4 ms |
| 1.000 | 8 | 3,961 s | 0,799 s | 7,983 s | 30,9 ms |
| 1.000 | 16 | 3,946 s | 0,783 s | 7,969 s | 56,4 ms |
| 1.000 | 32 | 4,532 s | 0,836 s | 8,546 s | 94,0 ms |

**Leitura provisória:** lotes de 1.000 reduziram o envio local de 6,055 s (100 × 1) para 3,961 s (1.000 × 8), cerca de 35%. Repeti três vezes os cenários de lote 1.000 com paralelismo 4, 8, 16 e 32. As medianas de envio foram **4,159 s**, **3,961 s**, **3,865 s** e **4,532 s**; os p95 HTTP medianos foram **22,4 ms**, **31,3 ms**, **50,0 ms** e **94,0 ms**, respectivamente. Não houve falhas HTTP. **16** foi ligeiramente mais rápido localmente, mas a diferença para 8 foi de apenas 96 ms (2,4%) e a latência p95 cresceu. Levar 8 e 16 para a rodada AWS; a escolha final exige medição no ambiente publicado, observando taxa de erro, p95, CPU da API e latência/capacidade do Atlas. O tempo até `pronto` varia com despacho da fila e polling de 2 s; compare principalmente `job_send_ms` para avaliar ingestão.

## Verificações funcionais locais

- CSV de 20.000 registros, lote de 1.000 e 8 chamadas simultâneas: `pronto`, 10.368.913 bytes e ETag multipart com **duas partes** no LocalStack.
- XLSX e JSON de 20.000 registros, lote de 1.000 e 8 chamadas simultâneas: `pronto`, ETags multipart com **duas** e **três** partes. Os objetos baixados passaram na leitura integral: ZIP XLSX válido com 20.001 linhas incluindo cabeçalho e array JSON válido com 20.000 registros.
- Solicitação XLSX de 1.000.001 registros, lotes de 1.000, 8 chamadas simultâneas e conteúdo de 1 caractere: `pronto`, resposta `formatoSolicitado=xlsx` e `formato=csv`, objeto S3 `text/csv`, 8.888.925 bytes e **1.000.002 linhas** no arquivo (cabeçalho mais dados). Este perfil curto serve para validar a mudança de formato, não para comparar desempenho com os testes de 512 caracteres.
- O fluxo completo também passou no PostgreSQL local: smoke XLSX/CSV/JSON, CSV de 20.000 registros com lotes de 1.000 e conversão XLSX → CSV acima de 1 milhão. Os resumos ficam em `benchmarks/ingestao-2026-10-04-local/postgres-check/`.
- Após adicionar telemetria do multipart, mais um CSV local de 20.000 registros terminou em **2 partes**, com `geracao_ms=390` e `upload_s3_ms=119` no log do worker. A métrica de geração cobre leitura do banco, escrita e upload; a de S3 soma as chamadas de envio e conclusão das partes.
- CSV local de 100.000 registros e 512 caracteres: arquivo texto de 51.888.914 bytes; uma cópia comprimida com gzip ficou com 29.549.084 bytes, redução de aproximadamente 43%. CSV já é texto simples. A compressão exige entregar `.csv.gz` ou descompactar no cliente; não foi ativada no contrato atual.

## AWS publicada — 2026-10-04, implementação nova

Imagem `2026-10-04-01` publicada na API, no worker e no portal; ECS ficou com uma task ativa de cada serviço, sem pendências, e os três deployments terminaram em `COMPLETED`. O smoke remoto passou para CSV, JSON, XLSX, lotes paralelos e validação de lote inválido. O OpenAPI publicado informa máximo de 1.000 itens por lote.

Matriz executada com k6 contra `https://apiga.vianadev.com.br`: um arquivo por rodada, 512 caracteres pseudoaleatórios por linha, corpo até 1 MiB, polling a cada 2 s. O tempo de envio vai do primeiro lote enviado ao último lote aceito; ponta a ponta vai do primeiro POST de criação até a primeira consulta GET que encontrou `pronto`. Os tempos de fila e geração vêm dos carimbos de tempo da API. Cada combinação teve **uma execução**, sem falhas HTTP; os valores não são médias. Os resumos estão em [`benchmarks/aws-2026-10-04-ingestao-csv-100k/`](../benchmarks/aws-2026-10-04-ingestao-csv-100k/) e [`benchmarks/aws-2026-10-04-ingestao-csv-1m/`](../benchmarks/aws-2026-10-04-ingestao-csv-1m/).

### CSV de 100.000 registros, 51.888.914 bytes

| Itens/lote | Lotes paralelos | Envio | Fila | Geração | Primeiro POST → GET `pronto` | HTTP p95 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 100 | 4 | 51,911 s | 0,708 s | 1,663 s | 56,868 s | 183 ms |
| 100 | 8 | 32,791 s | 0,824 s | 1,562 s | 37,733 s | 213 ms |
| 100 | 16 | 22,225 s | 1,509 s | 1,639 s | 27,158 s | 267 ms |
| 1.000 | 4 | 18,843 s | 1,557 s | 1,741 s | 23,767 s | 597 ms |
| 1.000 | 8 | 15,797 s | 0,646 s | 1,624 s | 20,739 s | 1.687 ms |
| 1.000 | 16 | 13,422 s | 0,119 s | 1,681 s | 16,224 s | 2.156 ms |
| 1.000 | 24 | 11,436 s | 1,136 s | 1,549 s | 16,374 s | 2.347 ms |
| 1.000 | 32 | 10,911 s | 1,106 s | 1,491 s | 15,866 s | 2.685 ms |

Repetir 100 × 8 na nova versão produziu envio de **32,791 s**, praticamente igual aos **32,591 s** da referência anterior. Assim, a diferença observada na mesma campanha entre 100 × 8 e 1.000 × 16 corresponde a envio **59,1% menor**; com 1.000 × 32, **66,7% menor**. Os p95 cresceram com o paralelismo, portanto o menor tempo por arquivo não implica maior capacidade sustentável de toda a API.

### CSV e JSON de 1.000.000 de registros

| Formato | Itens/lote | Lotes paralelos | Envio | Fila | Geração | Primeiro POST → GET `pronto` | HTTP p95 | Saída |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| CSV | 1.000 | 16 | 115,660 s | 0,400 s | 14,598 s | 131,230 s | 1.269 ms | 519.888.915 bytes |
| CSV | 1.000 | 32 | 98,642 s | 1,911 s | 13,796 s | 116,724 s | 2.089 ms | 519.888.915 bytes |
| JSON | 1.000 | 16 | 116,298 s | 0,240 s | 13,648 s | 131,880 s | 1.272 ms | 543.888.897 bytes |

Na versão anterior, com 100 × 8, o envio de 1 milhão de linhas levou **329,94 s em CSV** e **329,22 s em JSON**. Agora, 1.000 × 16 reduziu o envio em cerca de **65%** nos dois formatos; 1.000 × 32 reduziu o CSV em cerca de **70%**, com aumento do p95 HTTP de 1,27 s para 2,09 s em relação a 1.000 × 16. Comparações entre dias têm possíveis diferenças de rede e carga, ainda que o perfil de dados e a API sejam os mesmos.

O CloudWatch do worker confirmou o CSV de 1 milhão com **519.888.915 bytes em 8 partes** (`geracao_ms=13793`, `upload_s3_ms=5411`) e o JSON de 1 milhão com **543.888.897 bytes em 9 partes** (`geracao_ms=13642`, `upload_s3_ms=5247`). O upload de partes ocorre enquanto o arquivo é escrito; `geracao_ms` inclui leitura do banco, codificação e upload, e `upload_s3_ms` soma o tempo das chamadas S3. Esses números não devem ser somados.

### XLSX acima do limite

Um pedido de XLSX com **1.000.001 registros**, lotes de 1.000 e 8 envios simultâneos terminou em `pronto` como CSV: `formatoSolicitado=xlsx`, `formato=csv`, **8.888.925 bytes**, envio **26,967 s**, ponta a ponta **31,908 s**, zero falhas HTTP. O worker registrou geração em CSV e upload ao S3. Neste ensaio, cada linha tem **1 caractere** de conteúdo; a medida serve para validar a política de formato e não é comparável às linhas de 512 caracteres da matriz. Dados brutos em [`benchmarks/aws-2026-10-04-fallback-xlsx-csv/`](../benchmarks/aws-2026-10-04-fallback-xlsx-csv/).

### Decisão provisória de ingestão

O limite de aceitação da API agora é **1.000 registros por lote** e **1 MiB por requisição**; prevalece o menor dos dois. Para este perfil de 512 caracteres, **1.000 por lote e 16 lotes simultâneos** é o ponto inicial recomendado: terminou sem erros nos arquivos de 100 mil e 1 milhão, com p95 menor que a opção de 32. **32 lotes** reduziu ainda mais o envio de um arquivo isolado, mas elevou a latência HTTP. É preciso medir carga de vários clientes, CPU, banco e erro por mais tempo antes de declarar 32 como capacidade sustentada.

O worker publicado tem **uma task** e processa **um arquivo por vez**. Arquivos podem ser ingeridos ao mesmo tempo e aguardam na fila após `concluir`; a geração paralela exige aumentar tasks do worker e testar banco/S3. O limite de 30 minutos por trabalho, contado a partir de `concluir`, permanece. A carga de 1 bilhão de linhas não foi executada: mesmo com lotes de 1.000, seriam **1 milhão de chamadas de lote por arquivo**, e o prazo e a capacidade precisam ser revistos antes de chamar isso de suportado.
