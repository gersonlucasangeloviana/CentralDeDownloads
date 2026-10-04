# Teste de carga da API AWS — 2026-10-03

Alvo: `https://apiga.vianadev.com.br`, formato XLSX, 100 linhas por lote, polling a cada 2 s. A execução criou arquivos reais na API publicada e esperou cada um chegar a `pronto`. Todos os testes passaram com `job_success=100%` e `test_errors=0`.

| Carga | Envio de lotes | Ingestão até `concluir` | Fila | Geração | GET detectou `pronto` após concluir | Primeiro POST até `pronto` | Saída por arquivo |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 × 1.000 linhas, 128 caracteres, envio sequencial | 1,52 s | 2,08 s | 2,09 s | 0,12 s | 2,26 s | 4,33 s | 128.236 bytes |
| 1 × 100.000 linhas, 512 caracteres, envio sequencial | 2 min 44 s | 2 min 45 s | 0,52 s | 2,77 s | 4,38 s | 2 min 49 s | 45.706.594 bytes |
| 3 × 100.000 linhas, 512 caracteres, 4 lotes paralelos por arquivo | 49,84–50,08 s | 50,46–50,71 s | 0,99–5,88 s | 2,43–2,47 s | 4,39–8,65 s | 54,85–59,36 s | 45.702.632–45.708.957 bytes |
| 1 × 1.050.000 linhas, 512 caracteres, 4 lotes paralelos | 9 min 18,64 s | 9 min 19,22 s | 1,02 s | 25,28 s | 27,82 s | 9 min 47,04 s | 479.987.425 bytes |

No último teste, o k6 enviou 10.500 lotes (573 MB de tráfego enviado), fez 14 consultas de estado e recebeu `pronto` sem falhas HTTP. O tamanho final ficou em aproximadamente 480 MB decimais. O teste ultrapassa o limite de linhas de uma aba do Excel, mas não inspecionou o conteúdo do XLSX; o sucesso aqui confirma o estado e as contagens reportadas pela API.

IDs para consulta nos logs e no portal: 100 mil linhas sequenciais `eff59be474ec48649728924176f2a476`; três arquivos simultâneos `f792f479bac6403b9f4bde4007fae904`, `3598b1bf244643008f7ef1c21dca8068`, `d3f9d97032b3421eaaeda75e9514afec`; arquivo de 1,05 milhão `81ca9151fef04274acd6ced76c927cb0`.

As durações de fila e geração vêm das datas registradas pela API (`fechadoEm`, `iniciadoEm`, `prontoEm`). As durações de envio e ponta a ponta vêm do relógio do k6. A diferença entre `prontoEm` e a detecção pelo GET depende do intervalo de polling e da latência da rede.

Na amostra de 1 minuto do serviço ECS do worker durante a geração do arquivo maior (21:26 em `America/Sao_Paulo`), o CloudWatch registrou `CPUUtilization` máximo de **53,7%**, `MemoryUtilization` máximo de **18,6%** e `EphemeralStorageUtilized` de **0,47 GiB**. Como a geração durou apenas 25,28 s, essa amostragem pode não capturar os picos reais de memória e disco. A task estava configurada com 1 vCPU, 2 GiB de memória e 30 GiB de disco temporário.

**Leitura dos resultados:** neste volume, a ingestão dominou o tempo total. O worker gerou o arquivo de aproximadamente 480 MB em 25,28 s, sem atingir o prazo de 30 minutos. Três arquivos de aproximadamente 45,7 MB elevaram a espera na fila do primeiro para o último, como esperado para uma instância de worker que processa um arquivo por vez. Estes testes ainda não demonstram o limite de capacidade do worker; para encontrá-lo, aumente tamanho ou número de arquivos e acompanhe CPU, memória, disco temporário, banco, fila e S3 durante a execução.
