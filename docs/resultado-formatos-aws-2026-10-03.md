# Testes CSV e JSON na AWS — 2026-10-03

Alvo: `https://apiga.vianadev.com.br`. Cada arquivo recebeu 100 registros por lote, 512 caracteres pseudoaleatórios por linha, até 8 lotes simultâneos e polling do `GET /v1/arquivos/{id}` a cada 2 s. Os tempos abaixo são de uma execução por caso, com dados gerados pelo [script k6](../tests/worker-load.k6.js); não são médias de várias rodadas.

| Formato | Linhas | Lotes | Envio | Ingestão até `concluir` | Fila | Geração | Primeiro POST até GET `pronto` | Saída | GETs | Estado |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| CSV | 100.000 | 1.000 | 32,59 s | 33,35 s | 1,45 s | 1,93 s | 37,73 s | 51.888.914 bytes | 3 | `pronto` |
| JSON | 100.000 | 1.000 | 33,30 s | 33,86 s | 1,04 s | 1,59 s | 38,25 s | 54.288.896 bytes | 3 | `pronto` |
| CSV | 1.000.000 | 10.000 | 5 min 29,94 s | 5 min 30,50 s | 0,20 s | 16,68 s | 5 min 47,69 s | 519.888.915 bytes | 9 | `pronto` |
| JSON | 1.000.000 | 10.000 | 5 min 29,22 s | 5 min 30,30 s | 0,79 s | 15,04 s | 5 min 49,48 s | 543.888.897 bytes | 9 | `pronto` |

IDs dos arquivos concluídos: CSV 100 mil `8ddd5c0c0af349b599479d74c0744fba`; JSON 100 mil `0ec0a7232a2d404897c0a792d75a4647`; CSV 1 milhão `cd60e82c4fdf49ca8edccb50f5276849`; JSON 1 milhão `b4b9491fd75642e89e17af9dd582bd9f`.

O grupo CloudWatch `/ecs/gerador-excel-prod/worker` confirmou, para cada ID, a entrada `Gerando ... em <formato>` e a saída `Arquivo ... pronto com <bytes> bytes`. Os bytes registrados pelo worker coincidem com a tabela: 51.888.914 (CSV 100 mil), 54.288.896 (JSON 100 mil), 519.888.915 (CSV 1 milhão) e 543.888.897 (JSON 1 milhão). Os tempos da tabela usam os estados da API e o relógio do k6; a linha de log `Arquivo pronto` é emitida após a limpeza dos lotes.

Uma primeira tentativa de JSON com 1 milhão de linhas (`308195fc76c9437a9812983bb74e0023`) foi interrompida por perda de rede local. A API havia aceitado 6.488 lotes, ou 648.800 linhas, e o arquivo permaneceu em `recebendo`. O k6 não repetiu as chamadas de resultado incerto, porque a V1 não deduplica lotes. Essa tentativa **não** é uma medição de geração de 1 milhão de linhas. A nova execução com outro arquivo concluiu com sucesso e aparece na tabela.

No minuto em que o worker processou o CSV de 1 milhão (21:55, horário de São Paulo), o CloudWatch registrou `CPUUtilization` máximo de 72,21%, `MemoryUtilization` máximo de 17,19% e `EphemeralStorageUtilized` de 0,51 GiB. A geração durou 16,68 s; métricas agregadas por minuto podem não capturar o pico exato.

Para o JSON de 1 milhão (23:59), o CloudWatch registrou `CPUUtilization` máximo de 45,31%, `MemoryUtilization` máximo de 20,41% e `EphemeralStorageUtilized` de 0,52 GiB. A geração durou 15,04 s; vale a mesma ressalva sobre a amostragem por minuto.

## Um bilhão de linhas: análise de viabilidade

**Não executado.** A API aceita no máximo 100 registros por chamada, portanto 1 bilhão exigiria **10 milhões de chamadas `/lotes` por formato**. Com os mesmos 512 caracteres por linha, uma extrapolação linear dos arquivos medidos produz aproximadamente 457 GB para XLSX, 520 GB para CSV e 544 GB para JSON. São projeções, não arquivos gerados. A estimativa XLSX usa o [teste anterior de 1,05 milhão de linhas](resultado-teste-carga-aws-2026-10-03.md).

O worker AWS dispõe de 30 GiB de disco temporário, grava o arquivo inteiro nesse disco antes do upload ao S3 e tem prazo de 30 minutos após `concluir`. Cada saída projetada excede o disco por mais de uma ordem de grandeza. O envio de 1 bilhão de linhas também levaria dias no contrato atual: CSV e JSON com 1 milhão exigiram cerca de 5 min 30 s de ingestão, o que corresponderia a aproximadamente **92 horas por formato** se o ritmo fosse linear. Por esses limites concretos, disparar os três arquivos de 1 bilhão contra a API publicada não produziria um resultado válido de geração; exigiria mudar o contrato de ingestão e a estratégia de escrita/upload antes do teste.
