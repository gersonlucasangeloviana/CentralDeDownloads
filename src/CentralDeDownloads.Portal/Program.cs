using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var c = builder.Configuration;
var password = c["Portal:Password"] ?? "";
var apiKey = c["Api:Key"] ?? "";
var apiBase = c["Api:BaseUrl"] ?? "";
if (password.Length < 12 || apiKey.Length < 16 || !Uri.TryCreate(apiBase, UriKind.Absolute, out _))
    throw new InvalidOperationException("Configure Portal:Password, Api:Key e Api:BaseUrl.");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.LoginPath = "/login";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnRedirectToLogin = async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api/files") &&
            context.Request.Path.Value?.EndsWith("/download", StringComparison.OrdinalIgnoreCase) != true)
        {
            await context.HttpContext.SignOutAsync();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["X-Portal-Session-Expired"] = "1";
            return;
        }

        context.Response.Redirect(context.RedirectUri);
    };
});
builder.Services.AddAuthorization();
if (c["Proxy:KnownIpNetwork"] is { Length: > 0 } knownIpNetwork)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(knownIpNetwork));
    });
}
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddFixedWindowLimiter("login", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
builder.Services.AddHttpClient("api", client =>
{
    client.BaseAddress = new Uri(apiBase.TrimEnd('/') + "/");
    client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var app = builder.Build();
var indexHtml = File.ReadAllText(Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html"));
app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.Append("Referrer-Policy", "no-referrer");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery) => Results.Content("""
    <!doctype html><html lang="pt-BR"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
    <title>Central de Downloads · Entrar</title><style>body{font-family:system-ui;background:#f4f7fb;display:grid;place-items:center;min-height:100vh;margin:0}main{background:white;padding:2rem;border-radius:12px;box-shadow:0 8px 32px #1232;width:min(90vw,380px)}h1{font-size:1.35rem}label{display:block;margin:1rem 0 .4rem}input,button{box-sizing:border-box;width:100%;padding:.8rem;border-radius:6px;font:inherit}input{border:1px solid #bbc5d0}button{margin-top:1rem;background:#1654a3;color:white;border:0;cursor:pointer}</style>
    <main><h1>Central de Downloads</h1>{{SESSION_MESSAGE}}<form method="post" action="/login"><input type="hidden" name="__RequestVerificationToken" value="{{TOKEN}}"><label for="senha">Senha de administrador</label><input id="senha" name="senha" type="password" required autofocus><button>Entrar</button></form></main></html>
    """.Replace("{{TOKEN}}", HtmlEncoder.Default.Encode(antiforgery.GetAndStoreTokens(context).RequestToken!))
       .Replace("{{SESSION_MESSAGE}}", context.Request.Query["expirada"] == "1"
           ? "<p role=\"alert\">Sua sessão expirou. Entre novamente.</p>" : ""),
    "text/html; charset=utf-8"));
app.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
    var form = await context.Request.ReadFormAsync();
    var supplied = Encoding.UTF8.GetBytes(form["senha"].ToString());
    var expected = Encoding.UTF8.GetBytes(password);
    if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
        return Results.Redirect("/login?erro=1");
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "Administrador")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(new ClaimsPrincipal(identity));
    return Results.Redirect("/");
}).RequireRateLimiting("login");
app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
    await context.SignOutAsync();
    return Results.Redirect("/login");
}).RequireAuthorization();
app.MapGet("/", (HttpContext context, IAntiforgery antiforgery) => Results.Content(
    indexHtml.Replace("{{TOKEN}}", HtmlEncoder.Default.Encode(antiforgery.GetAndStoreTokens(context).RequestToken!)),
    "text/html; charset=utf-8")).RequireAuthorization();
app.MapGet("/api/files", async (HttpContext context, IHttpClientFactory factory, CancellationToken ct) =>
{
    var client = factory.CreateClient("api");
    using var response = await client.GetAsync("v1/arquivos" + context.Request.QueryString, ct);
    return Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json",
        statusCode: (int)response.StatusCode);
}).RequireAuthorization();
app.MapGet("/api/files/{id}", async (string id, IHttpClientFactory factory, CancellationToken ct) =>
{
    using var response = await factory.CreateClient("api").GetAsync($"v1/arquivos/{Uri.EscapeDataString(id)}", ct);
    return Results.Content(await response.Content.ReadAsStringAsync(ct), "application/json",
        statusCode: (int)response.StatusCode);
}).RequireAuthorization();
app.MapGet("/api/files/{id}/download", async (string id, IHttpClientFactory factory, CancellationToken ct) =>
{
    using var response = await factory.CreateClient("api").GetAsync($"v1/arquivos/{Uri.EscapeDataString(id)}/download", ct);
    return response.StatusCode == System.Net.HttpStatusCode.Redirect && response.Headers.Location is not null
        ? Results.Redirect(response.Headers.Location.ToString()) : Results.NotFound();
}).RequireAuthorization();
app.Run();
