var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => new
{
    service = "AgrocultivoWebSync",
    status = "online"
});

app.MapGet("/health", () => "OK");

app.Run();