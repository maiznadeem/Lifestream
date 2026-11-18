using Lifestream.Contracts;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Liveness only: must never open a database connection (the startup probe and the client warm-up call it).
app.MapGet(ApiRoutes.Healthz, () => Results.Ok(new { status = "healthy" }));

app.Run();

/// <summary>Entry point type, public so tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
