using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgrocultivoWebSync.Data;
using AgrocultivoWebSync.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ===============================
// CONEXIÓN A POSTGRESQL
// ===============================

var databaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");

if (string.IsNullOrWhiteSpace(databaseUrl))
{
    throw new InvalidOperationException(
        "No se encontró la variable DATABASE_URL");
}

var databaseUri = new Uri(databaseUrl);

var userInfo = databaseUri.UserInfo.Split(':', 2);

if (userInfo.Length != 2)
{
    throw new InvalidOperationException(
        "DATABASE_URL no contiene usuario y contraseña válidos.");
}

var connectionString =
    $"Host={databaseUri.Host};" +
    $"Port={databaseUri.Port};" +
    $"Database={databaseUri.AbsolutePath.TrimStart('/')};" +
    $"Username={Uri.UnescapeDataString(userInfo[0])};" +
    $"Password={Uri.UnescapeDataString(userInfo[1])};" +
    "SSL Mode=Prefer;Trust Server Certificate=true;";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

// ===============================
// PUERTO
// ===============================

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// ===============================
// ENDPOINTS BÁSICOS
// ===============================

app.MapGet("/", () => new
{
    service = "AgrocultivoWebSync",
    status = "online"
});

app.MapGet("/health", () => "OK");

// ===============================
// INICIAR CONEXIÓN QUICKBOOKS
// ===============================

app.MapGet("/auth/quickbooks", (IConfiguration config) =>
{
    var clientId = config["QUICKBOOKS_CLIENT_ID"];
    var redirectUri = config["QUICKBOOKS_REDIRECT_URI"];

    if (string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(redirectUri))
    {
        return Results.Problem(
            "Faltan QUICKBOOKS_CLIENT_ID o QUICKBOOKS_REDIRECT_URI.");
    }

    var state = Guid.NewGuid().ToString("N");

    var authorizationUrl =
        "https://appcenter.intuit.com/connect/oauth2" +
        $"?client_id={Uri.EscapeDataString(clientId)}" +
        "&response_type=code" +
        "&scope=com.intuit.quickbooks.accounting" +
        $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&state={Uri.EscapeDataString(state)}";

    return Results.Redirect(authorizationUrl);
});

// ===============================
// CALLBACK QUICKBOOKS
// ===============================

app.MapGet("/auth/quickbooks/callback", async (
    string? code,
    string? realmId,
    string? error,
    IConfiguration config,
    AppDbContext db) =>
{
    if (!string.IsNullOrWhiteSpace(error))
    {
        return Results.BadRequest(
            $"QuickBooks devolvió un error: {error}");
    }

    if (string.IsNullOrWhiteSpace(code) ||
        string.IsNullOrWhiteSpace(realmId))
    {
        return Results.BadRequest(
            "QuickBooks no devolvió code o realmId.");
    }

    var clientId = config["QUICKBOOKS_CLIENT_ID"];
    var clientSecret = config["QUICKBOOKS_CLIENT_SECRET"];
    var redirectUri = config["QUICKBOOKS_REDIRECT_URI"];

    if (string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(clientSecret) ||
        string.IsNullOrWhiteSpace(redirectUri))
    {
        return Results.Problem(
            "Faltan variables de configuración de QuickBooks.");
    }

    using var http = new HttpClient();

    var credentials =
        Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));

    http.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Basic", credentials);

    var form = new Dictionary<string, string>
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["redirect_uri"] = redirectUri
    };

    var response = await http.PostAsync(
        "https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer",
        new FormUrlEncodedContent(form));

    var body = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem(
            $"No se pudieron obtener los tokens de QuickBooks: {body}");
    }

    using var json = JsonDocument.Parse(body);

    var accessToken =
        json.RootElement
            .GetProperty("access_token")
            .GetString();

    var refreshToken =
        json.RootElement
            .GetProperty("refresh_token")
            .GetString();

    var expiresIn =
        json.RootElement
            .GetProperty("expires_in")
            .GetInt32();

    var refreshExpiresIn =
        json.RootElement.TryGetProperty(
            "x_refresh_token_expires_in",
            out var refreshExpiry)
                ? refreshExpiry.GetInt32()
                : 0;

    if (string.IsNullOrWhiteSpace(accessToken) ||
        string.IsNullOrWhiteSpace(refreshToken))
    {
        return Results.Problem(
            "QuickBooks no devolvió access_token o refresh_token.");
    }

    // ===============================
    // GUARDAR O ACTUALIZAR CONEXIÓN
    // ===============================

    var existing = await db.QuickBooksWebConnections
        .FirstOrDefaultAsync(x => x.RealmId == realmId);

    if (existing == null)
    {
        existing = new QuickBooksWebConnection
        {
            RealmId = realmId
        };

        db.QuickBooksWebConnections.Add(existing);
    }

    existing.AccessToken = accessToken;
    existing.RefreshToken = refreshToken;

    existing.AccessTokenExpiresAt =
        DateTime.UtcNow.AddSeconds(expiresIn);

    existing.RefreshTokenExpiresAt =
        refreshExpiresIn > 0
            ? DateTime.UtcNow.AddSeconds(refreshExpiresIn)
            : null;

    existing.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    return Results.Ok(new
    {
        conectado = true,
        realmId,
        mensaje = "QuickBooks conectado y guardado correctamente."
    });
});

app.MapGet("/quickbooks/products", async (
    AppDbContext db,
    IConfiguration config) =>
{
    var connection = await db.QuickBooksWebConnections
        .OrderByDescending(x => x.UpdatedAt)
        .FirstOrDefaultAsync();

    if (connection == null)
    {
        return Results.Problem(
            "No existe una conexión guardada de QuickBooks.");
    }

    using var http = new HttpClient();

    http.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue(
            "Bearer",
            connection.AccessToken);

    http.DefaultRequestHeaders.Accept.Add(
        new MediaTypeWithQualityHeaderValue("application/json"));

    var realmId = connection.RealmId;

    var query = "select * from Item maxresults 1000";

    var url =
        $"https://quickbooks.api.intuit.com/v3/company/{realmId}/query" +
        $"?query={Uri.EscapeDataString(query)}&minorversion=75";

    var response = await http.GetAsync(url);

    var body = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
    {
        return Results.Problem(
            $"Error consultando productos de QuickBooks: {body}");
    }

    using var json = JsonDocument.Parse(body);

    var productos = new List<object>();

    if (json.RootElement
        .TryGetProperty("QueryResponse", out var queryResponse) &&
        queryResponse.TryGetProperty("Item", out var items))
    {
        foreach (var item in items.EnumerateArray())
        {
            var id =
                item.TryGetProperty("Id", out var idValue)
                    ? idValue.GetString()
                    : null;

            var nombre =
                item.TryGetProperty("Name", out var nameValue)
                    ? nameValue.GetString()
                    : null;

            var sku =
                item.TryGetProperty("Sku", out var skuValue)
                    ? skuValue.GetString()
                    : null;

            var tipo =
                item.TryGetProperty("Type", out var typeValue)
                    ? typeValue.GetString()
                    : null;

            decimal? precio = null;

            if (item.TryGetProperty("UnitPrice", out var priceValue) &&
                priceValue.ValueKind == JsonValueKind.Number)
            {
                precio = priceValue.GetDecimal();
            }

            decimal? existencia = null;

            if (item.TryGetProperty("QtyOnHand", out var qtyValue) &&
                qtyValue.ValueKind == JsonValueKind.Number)
            {
                existencia = qtyValue.GetDecimal();
            }

            productos.Add(new
            {
                id,
                nombre,
                sku,
                tipo,
                precio,
                existencia
            });
        }
    }

    return Results.Ok(new
    {
        total = productos.Count,
        productos
    });
});

app.Run();