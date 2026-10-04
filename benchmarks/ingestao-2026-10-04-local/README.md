# Resultados brutos da matriz local

`resultados.jsonl` contém uma linha por execução com parâmetros, ID do trabalho, tempos, tamanho, taxa de erro e nomes dos arquivos correspondentes. Cada `*.json` é o resumo bruto do k6; cada `*.log` guarda a saída textual da execução. O arquivo `xlsx-1000001-fallback-csv.*` valida a conversão de formato com conteúdo curto e não integra a comparação de velocidade.

Os diretórios `xlsx-check/` e `json-check/` guardam as validações de 20.000 registros com multipart; também não integram a matriz CSV.
`postgres-check/` guarda o teste de 20.000 registros e a conversão de formato acima de 1 milhão com PostgreSQL.
`repeticao-2/` e `repeticao-3/` guardam as rodadas adicionais do lote de 1000 para escolher o paralelismo por mediana.

Ambiente e interpretação: [diário de comparação](../../docs/diario-comparativo-ingestao.md). Nenhum arquivo guarda a chave da API.
