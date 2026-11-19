using Lifestream.Api;
using Lifestream.Api.Infrastructure;

// `dotnet Lifestream.Api.dll --migrate` (the Container Apps migrate job): apply migrations, make sure the owner admin and demo data
// exist, then exit without starting the web host.
if (args.Contains("--migrate"))
{
    return await Migrator.RunAsync([.. args.Where(a => a != "--migrate")]);
}

var builder = WebApplication.CreateBuilder(args);
builder.AddLifestreamApi();
var app = builder.Build();
app.UseLifestreamApi();
app.MapLifestreamEndpoints();
app.Run();
return 0;

/// <summary>Entry point type, public so tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
