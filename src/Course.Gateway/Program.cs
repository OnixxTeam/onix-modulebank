var builder = WebApplication.CreateBuilder(args);

// Framework and proxy request logging default to Debug and would otherwise write
// headers (including Authorization) to the container log; Warning keeps both out
// without a per-header redaction list.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Yarp", LogLevel.Warning);

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

// Local: never reaches `api` or PostgreSQL, so it stays answerable while `api` is down.
app.MapGet("/health/live", () => Results.Ok());

// Everything else -- including /health/ready -- is whitelisted in the
// ReverseProxy config above; a path that matches no route falls through to the
// framework's default 404 rather than any handler here.
app.MapReverseProxy();

app.Run();
