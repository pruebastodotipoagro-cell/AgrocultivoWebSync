var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.MapGet("/", () => new
{
    service = "AgrocultivoWebSync",
    status = "online"
});

app.MapGet("/health", () => "OK");

app.Run();