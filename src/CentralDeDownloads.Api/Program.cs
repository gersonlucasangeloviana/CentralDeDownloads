using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CentralDeDownloads.Core;

var builder = WebApplication.CreateBuilder(args);
var c = builder.Configuration;
var apiKey = c["Security:ApiKey"] ?? "";
var signingKey = c["Security:DownloadSigningKey"] ?? "";
var publicBaseUrl = (c["PublicBaseUrl"] ?? "http://localhost:5000").TrimEnd('/');
var linkHours = Math.Clamp(c.GetValue("Download:LinkHours", 3), 1, 24);
if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicUri) ||
    publicUri.Scheme is not ("http" or "https") ||
    !string.IsNullOrEmpty(publicUri.UserInfo) || !string.IsNullOrEmpty(publicUri.Query) ||
    !string.IsNullOrEmpty(publicUri.Fragment) ||
    (!builder.Environment.IsDevelopment() && publicUri.Scheme != "https"))
    throw new InvalidOperationException("Configure PublicBaseUrl com a URL HTTPS pública da API.");
if (apiKey.Length < 16 || signingKey.Length < 32 || string.IsNullOrWhiteSpace(c["Aws:Bucket"]))
    throw new InvalidOperationException("Configure Security:ApiKey, Security:DownloadSigningKey e Aws:Bucket.");
builder.Services.AddSingleton<IExportStore>(_ => (c["Storage:Provider"] ?? "MongoDB").ToLowerInvariant() switch
{
    "mongodb" => new MongoExportStore(c["Mongo:ConnectionString"] ?? "mongodb://localhost:27017",
        c["Mongo:Database"] ?? "central_downloads"),
    "postgresql" => new PostgresExportStore(c["Postgres:ConnectionString"] ?? ""),
    _ => throw new InvalidOperationException("Storage:Provider deve ser MongoDB ou PostgreSQL.")
});
builder.Services.AddSingleton(new S3ExportStorage(c["Aws:Region"] ?? "us-east-1",
    c["Aws:Bucket"]!, c["Aws:ServiceUrl"]));
builder.Services.AddSingleton<IJobQueue>(_ => (c["Queue:Provider"] ?? "SQS").ToLowerInvariant() switch
{
    "sqs" => new SqsJobQueue(c["Aws:Region"] ?? "us-east-1", c["Aws:QueueUrl"] ?? "", c["Aws:ServiceUrl"]),
    "rabbitmq" => new RabbitMqJobQueue(c["RabbitMq:Uri"] ?? "", c["RabbitMq:QueueName"] ?? ""),
    _ => throw new InvalidOperationException("Queue:Provider deve ser SQS ou RabbitMQ.")
});
builder.Services.AddSingleton(new DownloadLinks(signingKey));
builder.Services.AddSingleton(sp => new NotificationSender(c["Resend:ApiKey"], c["Resend:From"],
    publicBaseUrl, sp.GetRequiredService<DownloadLinks>(), linkHours));
builder.Services.AddHostedService<DispatchService>();
builder.Services.AddHostedService<NotificationService>();
builder.Services.AddHostedService<JobCleanupService>();
builder.Services.AddHostedService<FileCleanupService>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(ApiOpenApi.Configure);
var app = builder.Build();
await app.Services.GetRequiredService<IExportStore>().EnsureIndexesAsync(CancellationToken.None);
_ = app.Services.GetRequiredService<IJobQueue>();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Saúde");
app.MapGet("/health/ready", async (IExportStore store, CancellationToken ct) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(3));
    try
    {
        await store.GetAsync("__health__", timeout.Token);
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
    { return Results.StatusCode(503); }
}).WithTags("Saúde");
var swaggerEnabled = c.GetValue("Swagger:Enabled", true);
if (swaggerEnabled)
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("../openapi/v1.json", "CentralDeDownloads API v1");
        options.DocumentTitle = "CentralDeDownloads — documentação da API";
        options.EnableDeepLinking();
    });
}
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/v1")) ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    await next();
});
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/v1/downloads") ||
        (swaggerEnabled && (ctx.Request.Path.StartsWithSegments("/swagger") ||
                            ctx.Request.Path.StartsWithSegments("/openapi"))))
    { await next(); return; }
    var supplied = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Api-Key"].ToString());
    var expected = Encoding.UTF8.GetBytes(apiKey);
    if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
    { ctx.Response.StatusCode = 401; return; }
    await next();
});

app.MapPost("/v1/arquivos", async (CreateJobRequest request, IExportStore store, CancellationToken ct) =>
{
    var format = request.Formato?.Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(request.Nome) || request.Nome.Length > 180 ||
        format is not ("xlsx" or "csv" or "json"))
        return Results.BadRequest(new { erro = "Nome ou formato inválido." });
    if (request.Periodo is not null && request.Periodo.Inicio > request.Periodo.Fim)
        return Results.BadRequest(new { erro = "Período inválido." });
    if (!NotificationSender.ValidEmail(request.Email))
        return Results.BadRequest(new { erro = "E-mail inválido." });
    if (!string.IsNullOrWhiteSpace(request.Email) &&
        (string.IsNullOrWhiteSpace(c["Resend:ApiKey"]) || string.IsNullOrWhiteSpace(c["Resend:From"])))
        return Results.Json(new { erro = "Notificação por e-mail indisponível." }, statusCode: 503);
    if (!string.IsNullOrWhiteSpace(request.Webhook))
    {
        try { await NotificationSender.ValidatePublicWebhookAsync(request.Webhook, ct); }
        catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException)
        { return Results.BadRequest(new { erro = ex.Message }); }
    }
    var job = new ExportJob
    {
        Name = request.Nome.Trim(),
        Format = format,
        RequestedFormat = format,
        PeriodStart = request.Periodo?.Inicio,
        PeriodEnd = request.Periodo?.Fim,
        Webhook = request.Webhook,
        Email = request.Email
    };
    await store.CreateAsync(job, ct);
    return Results.Created($"/v1/arquivos/{job.Id}", JobPresentation.ToResponse(job));
}).WithTags("Arquivos");

app.MapPost("/v1/arquivos/{id}/lotes", async (string id, HttpRequest request,
    IExportStore store, CancellationToken ct) =>
{
    const int maxBytes = 1024 * 1024;
    if (request.ContentLength > maxBytes)
        return Results.Json(new { erro = "Lote excede 1 MiB." }, statusCode: 413);
    try
    {
        var body = await ReadLimitedAsync(request.Body, maxBytes, ct);
        var input = BatchValidator.Parse(id, body);
        var (batch, error) = await store.AddBatchAsync(input, ct);
        if (batch is null) return Results.Conflict(new { erro = error });
        return Results.Accepted($"/v1/arquivos/{id}", new
        { id, idLote = batch.BatchId, sequencia = batch.Sequence, totalItens = batch.ItemCount });
    }
    catch (BodyTooLargeException)
    { return Results.Json(new { erro = "Lote excede 1 MiB." }, statusCode: 413); }
    catch (Exception ex) when (ex is ArgumentException or JsonException)
    { return Results.BadRequest(new { erro = ex.Message }); }
}).WithTags("Lotes");

app.MapPost("/v1/arquivos/{id}/concluir", async (string id, HttpRequest request,
    IExportStore store, CancellationToken ct) =>
{
    FinishRequest? finish = null;
    try
    {
        var body = await ReadLimitedAsync(request.Body, 2048, ct);
        if (body.Length > 0) finish = JsonSerializer.Deserialize<FinishRequest>(body.Span,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (BodyTooLargeException) { return Results.BadRequest(new { erro = "Corpo de conclusão muito grande." }); }
    catch (JsonException ex) { return Results.BadRequest(new { erro = ex.Message }); }
    if (finish?.TotalLotes < 0 || finish?.TotalItens < 0)
        return Results.BadRequest(new { erro = "Totais não podem ser negativos." });
    var job = await store.BeginCloseAsync(id, finish, ct);
    if (job is null) return Results.NotFound();
    if (job.Status == JobStatus.Closing) job = await store.FinalizeCloseAsync(id, ct) ?? job;
    if (job.Status == JobStatus.Failed) return Results.UnprocessableEntity(JobPresentation.ToResponse(job));
    return Results.Accepted($"/v1/arquivos/{id}", JobPresentation.ToResponse(job));
}).WithTags("Arquivos");

app.MapGet("/v1/arquivos/{id}", async (string id, IExportStore store, DownloadLinks links,
    CancellationToken ct) =>
{
    var job = await store.GetAsync(id, ct);
    if (job is null) return Results.NotFound();
    var link = job.Status == JobStatus.Ready && job.ExpiresAt > DateTime.UtcNow
        ? $"{publicBaseUrl}/v1/downloads/{links.Create(id, Min(job.ExpiresAt!.Value, DateTime.UtcNow.AddHours(linkHours)))}"
        : null;
    return Results.Ok(JobPresentation.ToResponse(job, link));
}).WithTags("Arquivos");

app.MapGet("/v1/arquivos", async (int? page, int? size, string? status, IExportStore store,
    CancellationToken ct) =>
{
    var p = Math.Max(1, page ?? 1);
    var s = Math.Clamp(size ?? 30, 1, 100);
    var jobs = await store.ListAsync(p, s, status, ct);
    return Results.Ok(new
    {
        pagina = p,
        tamanho = s,
        arquivos = jobs.Select(x => JobPresentation.ToResponse(x))
    });
}).WithTags("Arquivos");

app.MapGet("/v1/arquivos/{id}/download", async (string id, IExportStore store,
    S3ExportStorage storage, CancellationToken ct) => Download(await store.GetAsync(id, ct), storage))
    .WithTags("Downloads");
app.MapGet("/v1/downloads/{token}", async (string token, DownloadLinks links,
    IExportStore store, S3ExportStorage storage, CancellationToken ct) =>
{
    var id = links.Validate(token);
    return id is null ? Results.NotFound() : Download(await store.GetAsync(id, ct), storage);
}).WithTags("Downloads");
app.Run();

static IResult Download(ExportJob? job, S3ExportStorage storage) =>
    job is null || job.Status != JobStatus.Ready || job.ExpiresAt <= DateTime.UtcNow ||
    string.IsNullOrEmpty(job.S3Key) ? Results.NotFound() :
    Results.Redirect(storage.GetShortDownloadUrl(job.S3Key, $"{job.Name}.{job.Format}"));
static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
static async Task<ReadOnlyMemory<byte>> ReadLimitedAsync(Stream stream, int limit, CancellationToken ct)
{
    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    while (true)
    {
        var count = await stream.ReadAsync(chunk, ct);
        if (count == 0) return buffer.ToArray();
        if (buffer.Length + count > limit) throw new BodyTooLargeException();
        buffer.Write(chunk, 0, count);
    }
}
sealed class BodyTooLargeException : Exception;
public partial class Program;
