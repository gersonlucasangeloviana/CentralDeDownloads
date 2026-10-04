# Benchmarks da CentralDeDownloads

Os arquivos JSON/JSONL contêm os resumos e resultados estruturados; os arquivos `.log` guardam a saída do k6 ou do worker. Nenhum arquivo desta pasta contém a chave da API. O [diário comparativo](../docs/diario-comparativo-ingestao.md) explica as condições, os números e as limitações de cada ensaio; o [roteiro de teste](../docs/teste-carga-worker.md) mostra como executar uma nova rodada.

| Pasta | Ensaio |
| --- | --- |
| [`aws-2026-10-03/`](aws-2026-10-03/) | Referência anterior: CSV, JSON e XLSX na AWS. |
| [`ingestao-2026-10-04-local/`](ingestao-2026-10-04-local/) | Matriz local de lotes e paralelismo, verificações de multipart e formatos. |
| [`aws-2026-10-04-ingestao-csv-100k/`](aws-2026-10-04-ingestao-csv-100k/) | CSV de 100 mil registros na AWS. |
| [`aws-2026-10-04-ingestao-csv-1m/`](aws-2026-10-04-ingestao-csv-1m/) | CSV de 1 milhão de registros na AWS. |
| [`aws-2026-10-04-json-1m/`](aws-2026-10-04-json-1m/) | JSON de 1 milhão de registros na AWS. |
| [`aws-2026-10-04-fallback-xlsx-csv/`](aws-2026-10-04-fallback-xlsx-csv/) | Pedido XLSX com 1.000.001 registros convertido para CSV. |
| [`aws-2026-10-04-quatro-arquivos/`](aws-2026-10-04-quatro-arquivos/) | Quatro arquivos CSV de 100 mil registros enviados ao mesmo tempo. |
| [`aws-2026-10-04-csv-10m-curto/`](aws-2026-10-04-csv-10m-curto/) | CSV de 10 milhões de linhas curtas, com upload multipart ao S3. |

Os testes de **100 milhões** e **1 bilhão** de linhas não foram executados. As projeções e os limites conhecidos estão no diário comparativo.
