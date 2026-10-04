# Guia de uso da API

A API **gera** arquivos a partir de registros JSON enviados em lotes. `POST /v1/arquivos` cria uma solicitação; ele não recebe um arquivo binário. Depois da criação, envie os registros em `POST /v1/arquivos/{id}/lotes`, conclua o envio e consulte o resultado.

Para a lista interativa de rotas e respostas, use `/swagger`. Para executar dois fluxos completos, importe a [coleção Postman](../postman/README.md). As chamadas operacionais usam `Content-Type: application/json` nos corpos JSON e o cabeçalho `X-Api-Key: SUA_CHAVE`.

| Etapa | Rota | Resultado principal |
| --- | --- | --- |
| 1 | `POST /v1/arquivos` | Cria a solicitação e devolve `id` (`201`). |
| 2 | `POST /v1/arquivos/{id}/lotes` | Admite um lote de registros (`202`); repita conforme necessário. |
| 3 | `POST /v1/arquivos/{id}/concluir` | Encerra o recebimento e inicia a geração assíncrona (`202`). |
| 4 | `GET /v1/arquivos/{id}` | Consulta estado, erro e link de download (`200`). |
| Consulta | `GET /v1/arquivos` | Lista solicitações, com paginação e filtro (`200`). |
| Download | `GET /v1/arquivos/{id}/download` | Redireciona com `X-Api-Key` (`302`). |
| Download | `GET /v1/downloads/{token}` | Redireciona pelo link temporário, sem chave (`302`). |
| Saúde | `GET /health` e `GET /health/ready` | Verifica processo e conexão com banco. |

Antes de usar os exemplos `curl`, defina `API_URL` e `API_KEY` no terminal com a URL e a chave do seu ambiente. Substitua `ID_RECEBIDO` pelo `id` devolvido na criação e defina `LINK_DOWNLOAD` com o valor recebido na consulta. A chave é necessária em todas as rotas `/v1` **exceto** `/v1/downloads/{token}`. As duas rotas de saúde e a documentação também são públicas.

```bash
API_URL='http://localhost:5151'
API_KEY='valor-de-Security:ApiKey'
```

## Criar um arquivo: `POST /v1/arquivos`

| Campo | Obrigatório | Como preencher |
| --- | --- | --- |
| `nome` | Sim | Texto não vazio, até 180 caracteres. Aparece no portal e no nome do arquivo gerado. |
| `formato` | Sim | `xlsx`, `csv` ou `json`, **sem ponto**. A API ignora espaços externos e maiúsculas, e devolve o valor em minúsculas. |
| `periodo` | Não | Objeto com `inicio` e `fim` em `AAAA-MM-DD`; `inicio` deve ser menor ou igual a `fim`. É um metadado do trabalho: não filtra os registros enviados. |
| `webhook` | Não | URL HTTPS pública na porta 443, até 2048 caracteres. Recebe um `POST` com o estado final e o `linkDownload` quando houver. Use um endereço real, acessível pela API. |
| `email` | Não | Endereço de e-mail de até 254 caracteres. Requer Resend configurado na API; sem essa configuração, a criação retorna `503`. |

`webhook` e `email` podem ser usados isoladamente ou juntos. Se os dois forem informados, a API tenta enviar as duas notificações quando o trabalho termina, com sucesso ou falha.

### Exemplos de corpo

**XLSX básico**

```json
{
  "nome": "Vendas de setembro",
  "formato": "xlsx"
}
```

**CSV com período**

```json
{
  "nome": "Pedidos do mês",
  "formato": "csv",
  "periodo": {
    "inicio": "2026-09-01",
    "fim": "2026-09-30"
  }
}
```

**JSON básico**

```json
{
  "nome": "Pedidos para integração",
  "formato": "json"
}
```

**XLSX com todos os campos** (substitua o webhook e o e-mail por destinos reais):

```json
{
  "nome": "Vendas de setembro",
  "formato": "xlsx",
  "periodo": {
    "inicio": "2026-09-01",
    "fim": "2026-09-30"
  },
  "webhook": "https://seu-dominio-publico.com/arquivo",
  "email": "pessoa@seu-dominio.com"
}
```

Exemplo de chamada:

```bash
curl -i -X POST "$API_URL/v1/arquivos" \
  -H "X-Api-Key: $API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"nome":"Pedidos","formato":"csv"}'
```

A resposta `201 Created` inclui o cabeçalho `Location: /v1/arquivos/{id}` e um corpo como este:

```json
{
  "id": "c0ffee1234567890c0ffee1234567890",
  "nome": "Pedidos",
  "formato": "csv",
  "periodo": null,
  "status": "recebendo",
  "totalLotes": 0,
  "totalItens": 0,
  "criadoEm": "2026-09-30T12:00:00Z",
  "fechadoEm": null,
  "iniciadoEm": null,
  "prontoEm": null,
  "expiraEm": null,
  "tamanhoBytes": null,
  "erro": null,
  "linkDownload": null
}
```

Guarde `id` para todas as chamadas seguintes. `400` indica nome, formato, período, webhook ou e-mail inválido; `503` indica que um e-mail foi pedido sem o Resend configurado.

### O que muda entre os formatos

| `formato` | Arquivo produzido |
| --- | --- |
| `xlsx` | Planilha Excel até 1.000.000 de registros. Acima disso, a solicitação é convertida automaticamente para CSV ao concluir a ingestão. |
| `csv` | Texto UTF-8 com BOM, colunas separadas por `;`. Os valores são convertidos para texto e escapados quando necessário. |
| `json` | Um único array JSON com os objetos recebidos. Conserva os tipos simples e os campos de cada registro. |

O formato é solicitado na criação. Após `concluir`, a resposta informa `formato` (saída efetiva) e `formatoSolicitado` (pedido original). Os dados enviados continuam sendo JSON nos três casos: **não** envie conteúdo XLSX ou CSV no corpo de `/lotes`.

## Enviar registros: `POST /v1/arquivos/{id}/lotes`

Use o `id` da resposta `201` tanto na rota quanto no corpo. Exemplo:

```json
{
  "id": "id-retornado-na-criacao",
  "idLote": "lote-001",
  "dados": [
    { "Pedido": "PED-1001", "Valor": 149.9, "Pago": true },
    { "Pedido": "PED-1002", "Valor": 250.0, "Pago": false }
  ]
}
```

`idLote` é opcional, deve ser uma string não vazia de até 128 caracteres e serve para rastreamento. Ele **não** evita duplicações em reenvios. `dados` deve ter de 1 a 1000 objetos planos e o corpo inteiro pode ter até 1 MiB. Cada objeto precisa ter de 1 a 256 campos com nomes não vazios. Os valores podem ser string, número ou booleano; objetos, arrays e `null` não são aceitos. Para um valor sem conteúdo, envie `""`. Nomes de campos e valores string têm limite de 32767 caracteres e devem conter caracteres válidos para XLSX.

A primeira linha do primeiro lote **admitido pelo servidor** determina as colunas e sua ordem para XLSX e CSV. Nessas saídas, campos ausentes em linhas posteriores ficam vazios e campos adicionais são ignorados. No JSON, os objetos são preservados como enviados. Para obter colunas consistentes, envie as mesmas propriedades em todos os registros. Se enviar lotes em paralelo, a ordem de admissão pode diferir da ordem de envio.

Exemplo mínimo, sem `idLote`, que vale para qualquer `formato` criado:

```bash
curl -i -X POST "$API_URL/v1/arquivos/ID_RECEBIDO/lotes" \
  -H "X-Api-Key: $API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"id":"ID_RECEBIDO","dados":[{"Pedido":"PED-1001","Valor":149.9}]}'
```

A resposta `202 Accepted` informa a sequência atribuída pelo servidor e quantos registros foram aceitos **neste lote**:

```json
{
  "id": "c0ffee1234567890c0ffee1234567890",
  "idLote": null,
  "sequencia": 1,
  "totalItens": 1
}
```

Para mais registros, faça outra chamada com o mesmo `id` e até 1000 objetos em `dados`. Uma resposta `400` indica corpo inválido, como `id` divergente ou valor `null`; `409` significa arquivo inexistente ou já encerrado; `413` significa corpo acima de 1 MiB. Após uma resposta incerta, consulte as contagens do arquivo antes de decidir o que fazer: a V1 não tem reenvio idempotente.

## Concluir o recebimento: `POST /v1/arquivos/{id}/concluir`

Chame esta rota **depois que todos os lotes responderem**. Há três formas de enviar o corpo:

- Sem corpo: não verifica totais.
- Objeto vazio `{}`: não verifica totais.
- Objeto com `totalLotes`, `totalItens` ou ambos, inteiros não negativos: verifica os valores informados.

Exemplo com conferência:

```bash
curl -i -X POST "$API_URL/v1/arquivos/ID_RECEBIDO/concluir" \
  -H "X-Api-Key: $API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"totalLotes":1,"totalItens":1}'
```

Uma resposta `202 Accepted` devolve o objeto do arquivo, normalmente com `status: "na_fila"`. A geração continua em segundo plano. Se não houver lotes ou algum total for diferente do recebido, a API retorna `422` com `status: "falhou"` e `erro`, como `"Total de itens divergente."`; os lotes são descartados. Nesse caso, crie outra solicitação. `400` indica JSON inválido, corpo acima de 2 KiB ou total negativo; `404` indica ID inexistente.

## Consultar um arquivo: `GET /v1/arquivos/{id}`

```bash
curl -i "$API_URL/v1/arquivos/ID_RECEBIDO" \
  -H "X-Api-Key: $API_KEY"
```

Consulte após a conclusão até `status` ser `pronto` ou `falhou`. A consulta responde `200` com as contagens e as datas do trabalho. Exemplo de arquivo pronto:

```json
{
  "id": "c0ffee1234567890c0ffee1234567890",
  "nome": "Pedidos",
  "formato": "csv",
  "periodo": null,
  "status": "pronto",
  "totalLotes": 1,
  "totalItens": 1,
  "criadoEm": "2026-09-30T12:00:00Z",
  "fechadoEm": "2026-09-30T12:01:00Z",
  "iniciadoEm": "2026-09-30T12:01:02Z",
  "prontoEm": "2026-09-30T12:01:10Z",
  "expiraEm": "2026-10-01T12:01:10Z",
  "tamanhoBytes": 1024,
  "erro": null,
  "linkDownload": "https://api.seu-dominio.com/v1/downloads/TOKEN"
}
```

| `status` | O que significa | Próximo passo |
| --- | --- | --- |
| `recebendo` | Ainda aceita lotes. | Envie os lotes restantes e conclua. |
| `fechando` | Finaliza lotes admitidos em paralelo. | Consulte novamente. |
| `na_fila` | Geração aguardando o worker. | Consulte novamente. |
| `processando` | Worker gerando o arquivo. | Consulte novamente. |
| `pronto` | Arquivo disponível. | Abra `linkDownload` ou use a rota autenticada de download. |
| `falhou` | Geração ou validação falhou. | Leia `erro`; se necessário, crie outro trabalho. |
| `expirou` | Prazo de download encerrado. | Crie outro trabalho para gerar novamente. |

`periodo` é `null` quando não informado; as datas são horários UTC ou `null` enquanto a etapa correspondente não aconteceu. `erro` costuma ser `null` fora de `falhou`. `linkDownload` só aparece quando o arquivo está `pronto` e ainda válido. O arquivo pode ser baixado até 24 horas após `prontoEm`; um link emitido expira antes e pode ser renovado por outra consulta. Se o ID não existir, a rota retorna `404`.

## Listar arquivos: `GET /v1/arquivos`

```bash
curl -i "$API_URL/v1/arquivos?page=1&size=30&status=pronto" \
  -H "X-Api-Key: $API_KEY"
```

`page` começa em 1 e tem padrão 1. `size` tem padrão 30 e é ajustado para o intervalo de 1 a 100. `status` é opcional; omita-o para listar todos os estados, ou use um dos valores da tabela anterior. Os resultados vêm do mais recente para o mais antigo.

```json
{
  "pagina": 1,
  "tamanho": 30,
  "arquivos": [
    {
      "id": "c0ffee1234567890c0ffee1234567890",
      "nome": "Pedidos",
      "formato": "csv",
      "periodo": null,
      "status": "pronto",
      "totalLotes": 1,
      "totalItens": 1,
      "criadoEm": "2026-09-30T12:00:00Z",
      "fechadoEm": "2026-09-30T12:01:00Z",
      "iniciadoEm": "2026-09-30T12:01:02Z",
      "prontoEm": "2026-09-30T12:01:10Z",
      "expiraEm": "2026-10-01T12:01:10Z",
      "tamanhoBytes": 1024,
      "erro": null,
      "linkDownload": null
    }
  ]
}
```

`arquivos` pode ser `[]`. A lista **não fornece links de download**; consulte `GET /v1/arquivos/{id}` para obter um link atual.

## Baixar o resultado

**Com chave:** `GET /v1/arquivos/{id}/download` exige `X-Api-Key`. Quando o arquivo está pronto e válido, responde `302 Found` com uma URL S3 curta no cabeçalho `Location`.

```bash
curl -i "$API_URL/v1/arquivos/ID_RECEBIDO/download" \
  -H "X-Api-Key: $API_KEY"
```

Faça a requisição ao `Location` **sem** enviar a chave ao S3. Se o arquivo não existir, ainda não estiver pronto ou tiver expirado, a rota responde `404`.

**Pelo link temporário:** use o `linkDownload` completo recebido na consulta, webhook ou e-mail. Ele aponta para `GET /v1/downloads/{token}`, não exige chave e também responde `302` para o S3. Por não usar `X-Api-Key`, pode seguir o redirecionamento diretamente:

```bash
curl -L --fail "$LINK_DOWNLOAD" -o resultado.csv
```

Troque a extensão de `resultado.csv` conforme o `formato` escolhido. Trate o link como credencial de acesso: qualquer pessoa que o tiver pode baixar o arquivo enquanto ele for válido. Um token inválido ou expirado responde `404`.

## Saúde: `GET /health` e `GET /health/ready`

As duas rotas são públicas. `GET /health` responde `200 {"status":"ok"}` quando o processo está ativo, sem verificar o banco. `GET /health/ready` também verifica a conexão com o banco, com limite de 3 segundos; responde `200 {"status":"ok"}` quando pronto ou `503` sem corpo quando o banco está indisponível.

```bash
curl -i "$API_URL/health"
curl -i "$API_URL/health/ready"
```

## Notificações e erros comuns

Se `webhook` foi informado na criação, a API faz um `POST` para essa URL quando o trabalho termina. O corpo segue a mesma estrutura de `GET /v1/arquivos/{id}`: em sucesso, `status` é `pronto` e `linkDownload` pode ser usado; em falha, `status` é `falhou` e `erro` descreve a causa. O webhook não recebe assinatura ou autenticação na V1. O e-mail contém o link em sucesso ou o motivo em falha. As notificações são tentadas uma vez; falha de entrega não altera o estado do arquivo.

| Código | Situação típica |
| --- | --- |
| `400` | JSON ou campo inválido; leia `erro` quando presente. |
| `401` | `X-Api-Key` ausente ou incorreta em rota protegida. |
| `404` | ID inexistente ou download indisponível. |
| `409` | Tentativa de adicionar lote a arquivo encerrado ou inexistente. |
| `413` | Lote acima de 1 MiB. |
| `422` | Conclusão sem lotes ou com totais divergentes; o corpo mostra `status: "falhou"`. |
| `503` | E-mail pedido sem Resend configurado ou banco indisponível em `/health/ready`. |

Respostas de erros de modelo ou infraestrutura podem usar o formato padrão de erro do ASP.NET; trate sempre o código HTTP antes de ler `erro`.
