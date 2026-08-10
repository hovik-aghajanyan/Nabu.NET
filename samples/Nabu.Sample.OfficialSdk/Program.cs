using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nabu.Mcp.AspNetCore;
using Nabu.Sample.OfficialSdk.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<IBookCatalog, InMemoryBookCatalog>();

// ---------------------------------------------------------------------------
// Nabu discovers the [McpTool] actions, generates their schemas and replays
// each call through the pipeline - exactly as in the Todo sample. The one
// difference is UseOfficialMcpProtocol(): the wire protocol (transport,
// protocol revisions, sessions) is handled by the official ModelContextProtocol
// C# SDK instead of Nabu's built-in layer.
// ---------------------------------------------------------------------------
builder.Services.AddNabuMcp(options =>
{
    options.ServerName = "nabu-official-sdk-sample";
    options.ServerVersion = "1.0.0";
    options.Instructions = "A small book catalog. Search before adding to avoid duplicates.";
})
.UseOfficialMcpProtocol();
// The SDK's transport can be tuned through the optional delegate, for example
// to opt back into its session tracking:
//   .UseOfficialMcpProtocol(transport => transport.Stateless = false);

var app = builder.Build();

// Same mount point as the built-in layer; the path argument overrides the
// default /mcp route (options.Path works too, the argument wins).
app.UseNabuMcp("/books/mcp");

app.MapControllers();

app.Run();
