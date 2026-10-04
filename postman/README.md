# Fluxos Postman

Para as opções de preenchimento de `POST /v1/arquivos`, incluindo `formato`, período e notificações, consulte o [guia de uso da API](../docs/api.md).

Importe [CentralDeDownloads.postman_collection.json](CentralDeDownloads.postman_collection.json) no Postman. A coleção contém dois roteiros independentes:

| Pasta | Resultado | Passos |
| --- | --- | --- |
| `01 - Simples: CSV com 1 lote` | CSV com 2 pedidos | Criar, enviar 1 lote, concluir com totais, consultar até pronto e baixar |
| `02 - Completo: XLSX com 3 lotes` | XLSX com 9 pedidos | Criar com período, enviar 3 lotes, concluir com totais, consultar até pronto e baixar |

## Configuração

1. Em **Variables** da coleção, defina `baseUrl` com a URL da API, sem barra final. O valor inicial é `http://localhost:5151`, usado pelo perfil local `http` do projeto. Defina `apiKey` com o valor de `Security:ApiKey`. Não grave uma chave real no arquivo versionado.
2. Inicie API, worker, banco, fila e S3 (ou os emuladores locais). A API precisa ter `PublicBaseUrl` alcançável pelo Postman para que o link de download funcione.
3. Execute **uma pasta** no **Collection Runner**, em modo **Functional / Local**, com **1 iteração**, **Delay de 2000 ms** e **Follow redirects** ativado. Também é possível executar a coleção inteira; ela executa os dois roteiros em sequência.

Os scripts capturam automaticamente `simpleJobId`/`completeJobId`, conferem as respostas, repetem apenas a consulta de status até `pronto` e guardam `simpleDownloadUrl`/`completeDownloadUrl`. O limite é de 180 consultas por roteiro (aproximadamente 6 minutos com o delay indicado). Se a geração falhar ou passar desse limite, a execução para e mostra o erro. Se preferir enviar cada requisição manualmente, consulte o status novamente até `pronto`; `setNextRequest` só faz o loop no Collection Runner.

O download usa o `linkDownload` público retornado pela consulta, sem enviar `X-Api-Key` ao S3. O CSV usa `;` e o XLSX contém texto simples. Para testar JSON, altere `formato` na criação de uma cópia de uma pasta para `json`. Para testar webhook ou e-mail, acrescente os campos opcionais na criação e configure os serviços correspondentes.

Os dados dos lotes são exemplos de objetos planos: nenhuma propriedade contém `null`, array ou objeto. `idLote` no roteiro completo é apenas para rastreamento, sem deduplicação. Não reenvie lotes após uma resposta incerta: a V1 não oferece retry idempotente.

Para regenerar o arquivo após editar os exemplos de [build_collection.py](build_collection.py), execute `python3 postman/build_collection.py` na raiz do repositório.
