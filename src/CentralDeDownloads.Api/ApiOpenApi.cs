using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

internal static class ApiOpenApi
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddOperationTransformer((operation, context, _) =>
        {
            DescribeOperation(operation, context.Description.HttpMethod, context.Description.RelativePath);
            return Task.CompletedTask;
        });
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "CentralDeDownloads — API de arquivos",
                Version = "v1",
                Description = "Crie um arquivo, envie lotes JSON, conclua o envio e consulte o resultado. " +
                    "XLSX usa abas adicionais quando necessário; CSV usa ponto e vírgula; JSON gera um único array. " +
                    "O arquivo docs/api.md no repositório traz exemplos curl para todas as rotas. " +
                    "Para testar rotas protegidas, clique em Authorize " +
                    "e informe o valor de X-Api-Key. O linkDownload retornado quando pronto pode ser aberto sem essa chave."
            };
            document.Components ??= new OpenApiComponents();
            document.Components.Schemas?.Remove("CreateJobRequest");
            document.Components.Schemas?.Remove("PeriodRequest");
            document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
            {
                ["ApiKey"] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "X-Api-Key",
                    Description = "Chave configurada em Security:ApiKey. Informe somente o valor, sem prefixo Bearer."
                }
            };
            foreach (var (path, item) in document.Paths ?? [])
            {
                if (!path.StartsWith("/v1", StringComparison.Ordinal) ||
                    path.StartsWith("/v1/downloads/", StringComparison.Ordinal)) continue;
                if (item.Operations is null) continue;
                foreach (var operation in item.Operations.Values)
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("ApiKey", document)] = []
                    });
                }
            }
            return Task.CompletedTask;
        });
    }

    private static void DescribeOperation(OpenApiOperation operation, string? method, string? path)
    {
        switch ((method, path))
        {
            case ("POST", "v1/arquivos"):
                operation.OperationId = "CriarArquivo";
                operation.Summary = "1. Criar solicitação de arquivo";
                operation.Description = "Informe nome e formato. Período, webhook e e-mail são opcionais. " +
                    "O formato aceita xlsx, csv ou json (sem o ponto da extensão). " +
                    "XLSX com mais de 1.000.000 registros é convertido para CSV após concluir. " +
                    "Selecione um dos exemplos do corpo para ver cada formato e a combinação de campos opcionais. " +
                    "Se informar webhook e e-mail, ambos receberão uma notificação quando o trabalho terminar " +
                    "(com sucesso ou falha). Guarde o id devolvido para enviar os lotes. " +
                    "No exemplo completo, substitua webhook e e-mail por destinos reais. " +
                    "O webhook deve ser uma URL HTTPS pública na porta 443; o e-mail requer Resend configurado.";
                SetBody(operation, true, "nome e formato são obrigatórios; periodo, webhook e email podem ser omitidos.", CreateJobSchema(),
                    ("xlsx", "Planilha XLSX: campos obrigatórios", """{"nome":"Vendas de setembro","formato":"xlsx"}"""),
                    ("csv", "Arquivo CSV: campos obrigatórios", """{"nome":"Vendas de setembro","formato":"csv"}"""),
                    ("json", "Arquivo JSON: campos obrigatórios", """{"nome":"Vendas de setembro","formato":"json"}"""),
                    ("completo", "XLSX com período e notificações", """{"nome":"Vendas de setembro","formato":"xlsx","formatoSolicitado":"xlsx","periodo":{"inicio":"2026-09-01","fim":"2026-09-30"},"webhook":"https://seu-dominio-publico.com/arquivo","email":"pessoa@seu-dominio.com"}"""));
                SetResponse(operation, "201", "Solicitação criada. Use id nas próximas chamadas.",
                    """{"id":"c0ffee1234567890c0ffee1234567890","nome":"Vendas de setembro","formato":"xlsx","formatoSolicitado":"xlsx","periodo":null,"status":"recebendo","totalLotes":0,"totalItens":0,"criadoEm":"2026-09-30T12:00:00Z","fechadoEm":null,"iniciadoEm":null,"prontoEm":null,"expiraEm":null,"tamanhoBytes":null,"erro":null,"linkDownload":null}""");
                SetResponse(operation, "400", "Nome, formato, período, webhook ou e-mail inválido.", """{"erro":"Nome ou formato inválido."}""");
                SetResponse(operation, "503", "Notificação por e-mail solicitada sem configuração do Resend.", """{"erro":"Notificação por e-mail indisponível."}""");
                break;

            case ("POST", "v1/arquivos/{id}/lotes"):
                operation.OperationId = "EnviarLote";
                operation.Summary = "2. Enviar lote de registros";
                operation.Description = "Substitua o id da rota e do JSON pelo id recebido na criação; os dois devem ser iguais. " +
                    "dados deve ter de 1 a 1000 objetos planos, com até 256 campos por objeto. Cada valor pode ser " +
                    "texto, número ou booleano; não envie null, objetos ou arrays internos (use texto vazio para " +
                    "campos sem valor). O corpo inteiro tem limite de 1 MiB. A primeira linha do primeiro lote " +
                    "admitido define as colunas e sua ordem. Aguarde a resposta de todos os lotes, inclusive " +
                    "os enviados em paralelo, antes de concluir. idLote é opcional e não impede duplicações. " +
                    "Use o mesmo corpo nos três formatos de saída; o formato foi definido na criação.";
                DescribeParameter(operation, "id", "Id devolvido em POST /v1/arquivos. Deve ser igual ao campo id do corpo.");
                SetBody(operation, true, "id obrigatório e igual ao parâmetro da rota; idLote opcional; dados obrigatório (1–1000 registros).", BatchSchema(),
                    ("minimo", "Um registro, sem idLote", """{"id":"c0ffee1234567890c0ffee1234567890","dados":[{"Pedido":"123"}]}"""),
                    ("pedidos", "Dois registros com texto, número e booleano", """{"id":"c0ffee1234567890c0ffee1234567890","idLote":"lote-001","dados":[{"Pedido":"123","Cliente":"Ana","Valor":149.90,"Pago":true},{"Pedido":"124","Cliente":"Bia","Valor":250.00,"Pago":false}]}"""),
                    ("sem-valor", "Campo sem conteúdo: string vazia", """{"id":"c0ffee1234567890c0ffee1234567890","dados":[{"Pedido":"125","Observacao":"","Valor":0},{"Pedido":"126","Observacao":"","Valor":20.5}]}"""));
                SetResponse(operation, "202", "Lote admitido; sequencia é atribuída pelo servidor e pode diferir de idLote.",
                    """{"id":"c0ffee1234567890c0ffee1234567890","idLote":"lote-001","sequencia":1,"totalItens":2}""");
                SetResponse(operation, "400", "JSON inválido, id divergente, mais de 1000 registros ou valores complexos/null.",
                    """{"erro":"dados[0].Itens deve ser um valor simples."}""");
                SetResponse(operation, "409", "O arquivo não existe ou já foi encerrado.", """{"erro":"Arquivo inexistente ou encerrado."}""");
                SetResponse(operation, "413", "Corpo maior que 1 MiB.", """{"erro":"Lote excede 1 MiB."}""");
                break;

            case ("POST", "v1/arquivos/{id}/concluir"):
                operation.OperationId = "ConcluirArquivo";
                operation.Summary = "3. Concluir envio e iniciar geração";
                operation.Description = "Envie a chamada depois que todos os lotes responderem. O corpo pode ser " +
                    "completamente vazio ou conter totalLotes e/ou totalItens para conferência. Se um total " +
                    "informado divergir do recebido, o arquivo falha, os lotes são descartados e é necessário " +
                    "criar outra solicitação. Uma resposta 202 significa que a geração ainda é assíncrona.";
                DescribeParameter(operation, "id", "Id devolvido em POST /v1/arquivos.");
                SetBody(operation, false, "Opcional. Deixe o corpo vazio ou informe totalLotes e/ou totalItens (inteiros não negativos).", FinishSchema(),
                    ("com-totais", "Conferir lotes e itens", """{"totalLotes":2,"totalItens":150}"""),
                    ("apenas-itens", "Conferir somente os registros", """{"totalItens":150}"""),
                    ("sem-conferencia", "Sem totais: objeto vazio", "{}"));
                SetResponse(operation, "202", "Conclusão aceita; consulte o status até pronto ou falhou.",
                    """{"id":"c0ffee1234567890c0ffee1234567890","nome":"Vendas de setembro","formato":"xlsx","formatoSolicitado":"xlsx","periodo":null,"status":"na_fila","totalLotes":2,"totalItens":150,"criadoEm":"2026-09-30T12:00:00Z","fechadoEm":"2026-09-30T12:01:00Z","iniciadoEm":null,"prontoEm":null,"expiraEm":null,"tamanhoBytes":null,"erro":null,"linkDownload":null}""");
                SetResponse(operation, "400", "Corpo inválido, maior que 2 KiB ou totais negativos.", """{"erro":"Totais não podem ser negativos."}""");
                SetResponse(operation, "404", "Arquivo não encontrado.");
                SetResponse(operation, "422", "Sem lotes ou totais divergentes; arquivo marcado como falhou e lotes descartados.",
                    """{"id":"c0ffee1234567890c0ffee1234567890","nome":"Vendas de setembro","formato":"xlsx","formatoSolicitado":"xlsx","periodo":null,"status":"falhou","totalLotes":2,"totalItens":150,"criadoEm":"2026-09-30T12:00:00Z","fechadoEm":"2026-09-30T12:01:00Z","iniciadoEm":null,"prontoEm":null,"expiraEm":null,"tamanhoBytes":null,"erro":"Total de itens divergente.","linkDownload":null}""");
                break;

            case ("GET", "v1/arquivos/{id}"):
                operation.OperationId = "ConsultarArquivo";
                operation.Summary = "4. Consultar status e obter link";
                operation.Description = "Consulte pelo id da criação. Estados: recebendo, fechando, na_fila, " +
                    "processando, pronto, falhou e expirou. Se status=pronto, linkDownload contém um endereço " +
                    "temporário, válido por algumas horas e nunca além da validade do arquivo. Em falha, " +
                    "consulte erro. O arquivo fica disponível por até 24 horas desde prontoEm. " +
                    "Repita esta consulta até pronto ou falhou. Escolha um exemplo de resposta para ver cada estado.";
                DescribeParameter(operation, "id", "Id devolvido em POST /v1/arquivos.");
                SetResponseExamples(operation, "200", "Estado atual, contagens, mensagem de erro e link quando pronto.",
                    ("recebendo", "Aguardando lotes", """{"id":"c0ffee1234567890c0ffee1234567890","nome":"Vendas de setembro","formato":"xlsx","formatoSolicitado":"xlsx","periodo":null,"status":"recebendo","totalLotes":1,"totalItens":2,"criadoEm":"2026-09-30T12:00:00Z","fechadoEm":null,"iniciadoEm":null,"prontoEm":null,"expiraEm":null,"tamanhoBytes":null,"erro":null,"linkDownload":null}"""),
                    ("pronto", "Arquivo pronto para download", """{"id":"c0ffee1234567890c0ffee1234567890","nome":"Vendas de setembro","formato":"xlsx","formatoSolicitado":"xlsx","periodo":null,"status":"pronto","totalLotes":2,"totalItens":150,"criadoEm":"2026-09-30T12:00:00Z","fechadoEm":"2026-09-30T12:01:00Z","iniciadoEm":"2026-09-30T12:01:02Z","prontoEm":"2026-09-30T12:01:10Z","expiraEm":"2026-10-01T12:01:10Z","tamanhoBytes":10240,"erro":null,"linkDownload":"https://api.exemplo.com/v1/downloads/TOKEN"}"""),
                    ("falhou", "Geração ou conferência falhou", """{"id":"c0ffee1234567890c0ffee1234567890","nome":"Vendas de setembro","formato":"xlsx","formatoSolicitado":"xlsx","periodo":null,"status":"falhou","totalLotes":2,"totalItens":150,"criadoEm":"2026-09-30T12:00:00Z","fechadoEm":"2026-09-30T12:01:00Z","iniciadoEm":null,"prontoEm":null,"expiraEm":null,"tamanhoBytes":null,"erro":"Total de itens divergente.","linkDownload":null}"""));
                SetResponse(operation, "404", "Arquivo não encontrado.");
                break;

            case ("GET", "v1/arquivos"):
                operation.OperationId = "ListarArquivos";
                operation.Summary = "Listar arquivos";
                operation.Description = "Consulta paginada. page começa em 1 (padrão 1); size é limitado " +
                    "a 1–100 (padrão 30). status filtra pelo estado, por exemplo pronto ou falhou. " +
                    "Exemplo: GET /v1/arquivos?page=1&size=30&status=pronto. " +
                    "A lista vem da mais recente para a mais antiga e não contém linkDownload; consulte o id para obter um link atual.";
                DescribeParameter(operation, "page", "Número da página, a partir de 1. Padrão: 1.");
                DescribeParameter(operation, "size", "Itens por página. Valores fora de 1–100 são ajustados; padrão: 30.");
                DescribeParameter(operation, "status", "Filtro opcional: recebendo, fechando, na_fila, processando, pronto, falhou ou expirou.");
                SetResponseExamples(operation, "200", "Página de arquivos. Consulte cada id para obter um link atual.",
                    ("com-resultados", "Página com um arquivo", """{"pagina":1,"tamanho":30,"arquivos":[{"id":"c0ffee1234567890c0ffee1234567890","nome":"Vendas de setembro","formato":"xlsx","formatoSolicitado":"xlsx","periodo":null,"status":"pronto","totalLotes":2,"totalItens":150,"criadoEm":"2026-09-30T12:00:00Z","fechadoEm":"2026-09-30T12:01:00Z","iniciadoEm":"2026-09-30T12:01:02Z","prontoEm":"2026-09-30T12:01:10Z","expiraEm":"2026-10-01T12:01:10Z","tamanhoBytes":10240,"erro":null,"linkDownload":null}]}"""),
                    ("vazio", "Página sem resultados", """{"pagina":1,"tamanho":30,"arquivos":[]}"""));
                break;

            case ("GET", "v1/arquivos/{id}/download"):
                operation.OperationId = "BaixarArquivoAutenticado";
                operation.Summary = "Baixar arquivo com chave da API";
                operation.Description = "Exige X-Api-Key. Se o arquivo estiver pronto e ainda válido, " +
                    "responde com redirecionamento 302 e cabeçalho Location com uma URL S3 de curta duração. " +
                    "Faça a requisição ao Location sem encaminhar X-Api-Key ao S3.";
                DescribeParameter(operation, "id", "Id devolvido em POST /v1/arquivos.");
                SetResponse(operation, "302", "Redirecionamento para download no S3.");
                SetResponse(operation, "404", "Arquivo inexistente, não pronto ou expirado.");
                break;

            case ("GET", "v1/downloads/{token}"):
                operation.OperationId = "BaixarArquivoPorLink";
                operation.Summary = "Baixar pelo link temporário";
                operation.Description = "Não exige X-Api-Key. Use o linkDownload completo retornado " +
                    "pela consulta, webhook ou e-mail. Exemplo: GET /v1/downloads/TOKEN. " +
                    "A resposta 302 aponta para o S3; siga o redirecionamento sem chave da API. " +
                    "O token expira após algumas horas ou quando o arquivo expira.";
                DescribeParameter(operation, "token", "Token presente no fim de linkDownload. Prefira abrir o link completo.");
                SetResponse(operation, "302", "Redirecionamento para download no S3.");
                SetResponse(operation, "404", "Token inválido/expirado ou arquivo indisponível.");
                break;

            case ("GET", "health"):
                operation.Summary = "Verificar se o processo está ativo";
                operation.Description = "GET /health, sem X-Api-Key. Liveness; não verifica a conexão com o banco.";
                SetResponse(operation, "200", "Processo ativo.", """{"status":"ok"}""");
                break;

            case ("GET", "health/ready"):
                operation.Summary = "Verificar prontidão da API";
                operation.Description = "GET /health/ready, sem X-Api-Key. Readiness; verifica a conexão com o banco em até 3 segundos.";
                SetResponse(operation, "200", "API pronta para atender.", """{"status":"ok"}""");
                SetResponse(operation, "503", "Banco indisponível.");
                break;
        }

        if (path?.StartsWith("v1/", StringComparison.Ordinal) == true &&
            !path.StartsWith("v1/downloads/", StringComparison.Ordinal))
            SetResponse(operation, "401", "Cabeçalho X-Api-Key ausente ou inválido.");
    }

    private static void SetBody(OpenApiOperation operation, bool required, string description, OpenApiSchema schema,
        params (string Name, string Summary, string Json)[] examples)
    {
        OpenApiMediaType? media = null;
        operation.RequestBody?.Content?.TryGetValue("application/json", out media);
        media ??= new OpenApiMediaType();
        media.Schema = schema;
        media.Examples = examples.ToDictionary(x => x.Name, x => (IOpenApiExample)new OpenApiExample
        {
            Summary = x.Summary,
            Value = JsonNode.Parse(x.Json)
        });
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = required,
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType> { ["application/json"] = media }
        };
    }

    private static void SetResponse(OpenApiOperation operation, string code, string description, string? json = null)
    {
        operation.Responses ??= new OpenApiResponses();
        if (code is "201" or "202" or "302") operation.Responses.Remove("200");
        operation.Responses[code] = new OpenApiResponse
        {
            Description = description,
            Content = json is null ? null : new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new() { Example = JsonNode.Parse(json), Schema = new OpenApiSchema { Type = JsonSchemaType.Object } }
            }
        };
    }

    private static void SetResponseExamples(OpenApiOperation operation, string code, string description,
        params (string Name, string Summary, string Json)[] examples)
    {
        operation.Responses ??= new OpenApiResponses();
        operation.Responses[code] = new OpenApiResponse
        {
            Description = description,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new()
                {
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Object },
                    Examples = examples.ToDictionary(x => x.Name, x => (IOpenApiExample)new OpenApiExample
                    {
                        Summary = x.Summary,
                        Value = JsonNode.Parse(x.Json)
                    })
                }
            }
        };
    }

    private static void DescribeParameter(OpenApiOperation operation, string name, string description)
    {
        var parameter = operation.Parameters?.FirstOrDefault(x => x.Name == name);
        if (parameter is not null) parameter.Description = description;
    }

    private static OpenApiSchema CreateJobSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Required = new HashSet<string> { "nome", "formato" },
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["nome"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MaxLength = 180,
                Description = "Nome exibido no portal e usado no arquivo final. Não pode ficar em branco."
            },
            ["formato"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Formato do arquivo final, sem ponto: xlsx (planilha), csv (texto separado por ponto e vírgula) ou json (array de objetos). A API aceita letras maiúsculas e normaliza para minúsculas.",
                Enum = [JsonValue.Create("xlsx")!, JsonValue.Create("csv")!, JsonValue.Create("json")!]
            },
            ["periodo"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Description = "Opcional. Intervalo de datas do relatório; inicio deve ser anterior ou igual a fim.",
                Required = new HashSet<string> { "inicio", "fim" },
                Properties = new Dictionary<string, IOpenApiSchema>
                {
                    ["inicio"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date", Description = "Data inicial: AAAA-MM-DD." },
                    ["fim"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "date", Description = "Data final: AAAA-MM-DD." }
                }
            },
            ["webhook"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uri",
                MaxLength = 2048,
                Description = "Opcional. HTTPS público na porta 443; recebe POST com status e linkDownload quando terminar."
            },
            ["email"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "email",
                MaxLength = 254,
                Description = "Opcional. Recebe notificação via Resend quando terminar; exige Resend configurado."
            }
        }
    };

    private static OpenApiSchema BatchSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Required = new HashSet<string> { "id", "dados" },
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["id"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Description = "Id devolvido por POST /v1/arquivos; deve coincidir com o id da rota."
            },
            ["idLote"] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                MaxLength = 128,
                Description = "Identificador opcional para rastreamento. Reenvios não são deduplicados."
            },
            ["dados"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                MinItems = 1,
                MaxItems = 1000,
                Description = "De 1 a 1000 registros. O corpo completo da requisição tem limite de 1 MiB.",
                Items = new OpenApiSchema
                {
                    Type = JsonSchemaType.Object,
                    MinProperties = 1,
                    MaxProperties = 256,
                    Description = "Objeto plano. Mantenha as mesmas colunas em todos os registros; a primeira linha do primeiro lote define a ordem.",
                    AdditionalProperties = new OpenApiSchema
                    {
                        Type = JsonSchemaType.String | JsonSchemaType.Integer | JsonSchemaType.Number | JsonSchemaType.Boolean,
                        MaxLength = 32767,
                        Description = "Valor simples: texto, número ou booleano. Sem null, arrays ou objetos."
                    }
                }
            }
        }
    };

    private static OpenApiSchema FinishSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Corpo opcional; também é válido enviar a requisição sem corpo.",
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["totalLotes"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int64",
                Minimum = "0",
                Description = "Opcional. Número de lotes aceitos; divergência marca o arquivo como falhou."
            },
            ["totalItens"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Integer,
                Format = "int64",
                Minimum = "0",
                Description = "Opcional. Soma de registros aceitos; divergência marca o arquivo como falhou."
            }
        }
    };
}
