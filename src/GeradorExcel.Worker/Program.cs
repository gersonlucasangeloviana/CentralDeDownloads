using GeradorExcel.Core;
using GeradorExcel.Worker;

var builder = Host.CreateApplicationBuilder(args);
var c = builder.Configuration;
if (string.IsNullOrWhiteSpace(c["Aws:Bucket"]))
    throw new InvalidOperationException("Configure Aws:Bucket.");
builder.Services.AddSingleton<IExportStore>(_ => (c["Storage:Provider"] ?? "MongoDB").ToLowerInvariant() switch
{
    "mongodb" => new MongoExportStore(c["Mongo:ConnectionString"] ?? "mongodb://localhost:27017",
        c["Mongo:Database"] ?? "gerador_excel"),
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
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
