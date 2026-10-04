using System.Net;
using System.Net.Mail;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace CentralDeDownloads.Core;

public static class JobPresentation
{
    public static object ToResponse(ExportJob job, string? downloadUrl = null) => new
    {
        id = job.Id,
        nome = job.Name,
        formato = job.Format,
        formatoSolicitado = job.RequestedFormat ?? job.Format,
        periodo = job.PeriodStart is null ? null : new { inicio = job.PeriodStart, fim = job.PeriodEnd },
        status = job.Status,
        totalLotes = job.BatchCount,
        totalItens = job.ItemCount,
        criadoEm = job.CreatedAt,
        fechadoEm = job.ClosedAt,
        iniciadoEm = job.StartedAt,
        prontoEm = job.ReadyAt,
        expiraEm = job.ExpiresAt,
        tamanhoBytes = job.FileBytes,
        erro = job.Error,
        linkDownload = downloadUrl
    };
}

public sealed class NotificationSender(string? resendApiKey, string? emailFrom,
    string publicBaseUrl, DownloadLinks links, int linkHours)
{
    private static readonly HttpClient WebhookClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = ConnectPublicAsync
    })
    { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly HttpClient ResendClient = new()
    { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<string?> SendAsync(ExportJob job, CancellationToken ct)
    {
        var errors = new List<string>();
        var link = job.Status == JobStatus.Ready && job.ExpiresAt is not null
            ? $"{publicBaseUrl.TrimEnd('/')}/v1/downloads/{links.Create(job.Id, Min(job.ExpiresAt.Value, DateTime.UtcNow.AddHours(linkHours)))}"
            : null;
        var payload = JobPresentation.ToResponse(job, link);
        if (!string.IsNullOrWhiteSpace(job.Webhook))
        {
            try
            {
                await ValidatePublicWebhookAsync(job.Webhook, ct);
                using var request = new HttpRequestMessage(HttpMethod.Post, job.Webhook)
                { Content = JsonContent.Create(payload) };
                using var response = await WebhookClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode) errors.Add($"Webhook respondeu {(int)response.StatusCode}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ArgumentException or System.Net.Sockets.SocketException)
            { errors.Add($"Webhook: {ex.Message}"); }
        }
        if (!string.IsNullOrWhiteSpace(job.Email))
        {
            if (string.IsNullOrWhiteSpace(resendApiKey) || string.IsNullOrWhiteSpace(emailFrom))
                errors.Add("Resend não configurado.");
            else
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resendApiKey);
                    var subject = job.Status == JobStatus.Ready ? $"Arquivo pronto: {job.Name}" : $"Falha no arquivo: {job.Name}";
                    var conversion = job.RequestedFormat is not null && job.RequestedFormat != job.Format
                        ? $" O formato solicitado ({job.RequestedFormat}) foi convertido para {job.Format} por superar 1 milhão de registros."
                        : "";
                    var body = job.Status == JobStatus.Ready
                        ? $"O arquivo {job.Name} está pronto em {job.Format}.{conversion} Link: {link}"
                        : $"O arquivo {job.Name} falhou. Motivo: {job.Error}";
                    request.Content = JsonContent.Create(new { from = emailFrom, to = new[] { job.Email }, subject, text = body });
                    using var response = await ResendClient.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!response.IsSuccessStatusCode) errors.Add($"Resend respondeu {(int)response.StatusCode}.");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                { errors.Add($"Resend: {ex.Message}"); }
            }
        }
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private static DateTime Min(DateTime first, DateTime second) => first < second ? first : second;

    public static bool ValidEmail(string? value) => value is null ||
        (value.Length <= 254 && (string.IsNullOrWhiteSpace(value) ||
            MailAddress.TryCreate(value, out var address) && address.Address == value));

    public static async Task ValidatePublicWebhookAsync(string value, CancellationToken ct)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Port != 443)
            throw new ArgumentException("Webhook deve ser uma URL HTTPS pública na porta 443.");
        await ResolvePublicAddressesAsync(uri.Host, ct);
    }

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await ResolvePublicAddressesAsync(context.DnsEndPoint.Host, ct);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        if (addresses.Length == 0 || addresses.Any(IsPrivate))
            throw new ArgumentException("Webhook deve resolver apenas para endereços públicos.");
        return addresses;
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPrivate(address.MapToIPv4());
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.GetAddressBytes()[0] is 0xfc or 0xfd;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 ||
            bytes[0] == 169 && bytes[1] == 254 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 192 && bytes[1] == 0 ||
            bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
            bytes[0] == 198 && bytes[1] is 18 or 19 ||
            bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
            bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 ||
            bytes[0] >= 224;
    }
}
