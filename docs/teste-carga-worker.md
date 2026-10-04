# Teste de carga do worker com k6

O script [worker-load.k6.js](../tests/worker-load.k6.js) executa o fluxo completo da API: cria uma solicitação, envia registros JSON em lotes, conclui o recebimento e consulta o estado até `pronto` ou `falhou`. A API não recebe um XLSX binário; o worker gera o arquivo a partir desses lotes.

Cada VU do k6 cria **um arquivo**. `FILES=1` mede um arquivo grande; `FILES=3` coloca três arquivos na fila. O worker atual processa um arquivo por vez por instância, então a espera dos demais é parte esperada do teste. Os dados têm conteúdo hexadecimal diferente por linha para evitar um XLSX artificialmente pequeno por compressão.

## Executar

Com API, worker, banco, fila e S3 (ou emuladores) em execução, instale o [k6](https://grafana.com/docs/k6/latest/set-up/install-k6/) e exporte a URL e a chave da API no terminal:

```bash
export CENTRAL_DOWNLOADS_API_URL='http://localhost:5080'
export CENTRAL_DOWNLOADS_API_KEY='sua-chave'
k6 run tests/worker-load.k6.js
```

O padrão cria um XLSX de 1.000 linhas, em 10 lotes de 100, com 128 caracteres de conteúdo por linha. Aumente em etapas:

```bash
FILES=1 ROWS_PER_FILE=100000 PAYLOAD_CHARS=512 k6 run tests/worker-load.k6.js
FILES=1 ROWS_PER_FILE=1000000 ROWS_PER_BATCH=1000 BATCH_CONCURRENCY=8 PAYLOAD_CHARS=512 k6 run tests/worker-load.k6.js
FILES=1 ROWS_PER_FILE=1050000 ROWS_PER_BATCH=1000 BATCH_CONCURRENCY=8 PAYLOAD_CHARS=512 FORMAT=csv k6 run tests/worker-load.k6.js
FILES=3 ROWS_PER_FILE=100000 PAYLOAD_CHARS=512 k6 run tests/worker-load.k6.js
```

Para reduzir o tempo de ingestão dos testes maiores, compare lotes de 100 e 1000 registros com `BATCH_CONCURRENCY=1,4,8,16`. O script só conclui depois de receber todas as respostas. A matriz automatizada guarda um resumo JSON e um log por cenário:

```bash
python3 tests/run-ingestion-benchmark.py --rows 100000 --sizes 100,1000 --concurrency 1,4,8,16 --format csv --output benchmarks/nova-rodada
```

O [diário comparativo](diario-comparativo-ingestao.md) guarda configuração, resultados e observações. Use outro diretório por rodada para preservar os arquivos brutos.

Para a API publicada, defina `CENTRAL_DOWNLOADS_API_URL='https://apiga.vianadev.com.br'` e use a chave desse ambiente. Faça primeiro o teste padrão de 1.000 linhas; depois aumente para 10.000, 100.000 e vários arquivos, observando os recursos do worker em cada etapa. O `GET /v1/arquivos/{id}` é consultado repetidamente até detectar `pronto` ou `falhou`. O script registra cada mudança de estado e o número de consultas.

O primeiro exemplo grande atinge o limite configurado para XLSX (1 milhão de registros). Acima desse total, o formato solicitado `xlsx` é convertido automaticamente para `csv` depois de `concluir`. O script confere estado, contagens e tamanho, mas não baixa o resultado. Comece com o teste padrão e aumente somente depois de medir o caso anterior.

| Variável | Padrão | Uso |
| --- | ---: | --- |
| `FILES` | 1 | Número de arquivos/VUs simultâneos; de 1 a 50. |
| `ROWS_PER_FILE` | 1000 | Linhas em cada arquivo. |
| `ROWS_PER_BATCH` | 100 | Linhas por chamada, máximo da API: 1000. |
| `BATCH_CONCURRENCY` | 1 | Chamadas de lote simultâneas por arquivo, de 1 a 32. |
| `PAYLOAD_CHARS` | 128 | Caracteres ASCII pseudoaleatórios por linha; máximo do script: 8000. |
| `FORMAT` | `xlsx` | `xlsx`, `csv` ou `json`. |
| `POLL_SECONDS` | 5 | Intervalo de consulta do estado. |
| `MAX_WAIT_SECONDS` | 2100 | Espera pelo resultado após `concluir`. |
| `MAX_DURATION` | `90m` | Limite de duração do cenário k6, incluindo ingestão e espera. |

Cada lote também precisa caber em **1 MiB**. O script confere o tamanho antes do envio; reduza `PAYLOAD_CHARS` ou `ROWS_PER_BATCH` se ultrapassar o limite. O número de bytes do XLSX final depende da compressão e da estrutura XML; `PAYLOAD_CHARS` não determina diretamente o tamanho final.

## Ler o resultado

O k6 mostra `job_create_ms` (criação), `job_send_ms` (geração e envio dos lotes), `job_close_ms` (conclusão), `job_ingest_ms` (início da criação → resposta da conclusão), `job_queue_ms` (`fechadoEm` → `iniciadoEm`), `job_generation_ms` (`iniciadoEm` → `prontoEm`), `job_total_ms` (conclusão → pronto segundo o servidor), `job_polling_ms` (resposta da conclusão → GET que detectou `pronto`), `job_end_to_end_ms` (primeiro POST → GET que detectou `pronto`), `job_polls` (consultas por arquivo) e `job_output_bytes`. As durações baseadas em datas do servidor e as durações observadas pelo cliente podem diferir até o intervalo de polling. A linha de log de cada arquivo inclui ID, linhas, lotes, bytes e tempos. `job_success` precisa ser 100% e `test_errors` precisa ser zero; qualquer erro faz o teste falhar. O script **não baixa** o resultado, para não misturar tráfego S3 com a medição de geração.

Compare as métricas entre tamanhos e números de arquivos. O limite prático aparece quando o tempo de geração sobe muito, a fila cresce sem esvaziar, há arquivos `falhou`, o prazo de 30 minutos é atingido, ou CPU, memória, banco ou S3 saturam. No ECS, acompanhe CPU e memória da task do worker, logs, idade e profundidade da fila, além das métricas da API, banco e S3. O worker envia a saída diretamente ao S3 em partes de 64 MiB por padrão (`Aws__UploadPartMiB`); não grava o arquivo completo no disco temporário. O Terraform reserva **1 vCPU, 2 GiB de RAM e 30 GiB de disco temporário** para a task; essas reservas não são um resultado de benchmark.

Resultados contra a API AWS publicada em 2026-10-03: [XLSX e concorrência](resultado-teste-carga-aws-2026-10-03.md) e [CSV/JSON com 100 mil e 1 milhão de registros](resultado-formatos-aws-2026-10-03.md). Os [resumos brutos do k6](../benchmarks/aws-2026-10-03/README.md) também estão no repositório.

Não há retry de lotes na V1. Se uma chamada der timeout depois de ser aceita pelo servidor, o script encerra aquele arquivo sem reenviar o lote; uma repetição poderia duplicar linhas. Solicitações deixadas em `recebendo` por erro de ingestão são limpas pela rotina de inatividade. Testes de carga criam dados e objetos reais no banco e no S3 até a limpeza normal do sistema.
