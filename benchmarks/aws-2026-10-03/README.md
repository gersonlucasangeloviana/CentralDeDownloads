# Resumos brutos do k6 — API AWS

Estes arquivos são os JSONs gerados por `k6 run --summary-export` nos testes descritos em [resultado-formatos-aws-2026-10-03.md](../../docs/resultado-formatos-aws-2026-10-03.md) e no [teste XLSX anterior](../../docs/resultado-teste-carga-aws-2026-10-03.md). Não contêm a chave da API.

| Arquivo | Execução |
| --- | --- |
| `csv-100k.json` | CSV, 100 mil linhas, concluído. |
| `json-100k.json` | JSON, 100 mil linhas, concluído. |
| `csv-1m.json` | CSV, 1 milhão de linhas, concluído. |
| `json-1m.json` | JSON, 1 milhão de linhas, concluído em novo arquivo após a perda de rede. |
| `json-1m-interrompido.json` | Tentativa interrompida pela rede local após 648.800 linhas aceitas; não mede geração. |
| `xlsx-1050k.json` | XLSX, 1,05 milhão de linhas, concluído. |

Os JSONs guardam métricas agregadas. IDs dos arquivos, estados observados e métricas do CloudWatch estão nos relatórios e nas notas dos slides finais da apresentação.
