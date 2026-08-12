# Nabu.NET — turn your ASP.NET Core Web API into an MCP server with one attribute

> Ready-to-publish gist. Create it at https://gist.github.com with the filename
> `nabu-net-aspnetcore-mcp-server.md` and the description:
> "Turn an existing ASP.NET Core Web API into an MCP server with one attribute — Nabu.Mcp.AspNetCore quickstart".

**Repo:** https://github.com/hovik-aghajanyan/nabu.net · **Docs:** https://hovik-aghajanyan.github.io/nabu.net/ · **NuGet:** https://www.nuget.org/packages/Nabu.Mcp.AspNetCore

[`Nabu.Mcp.AspNetCore`](https://github.com/hovik-aghajanyan/nabu.net) publishes existing ASP.NET Core
controller actions and Minimal API route handlers as [Model Context Protocol](https://modelcontextprotocol.io)
tools — without rewriting the API. Every tool call is replayed as a real HTTP request through **your
application's own pipeline**, so authentication, authorization, validation, filters and middleware keep
working exactly as they do over HTTP.

## The entire integration

```csharp
[HttpGet("{id:guid}")]
[McpTool]                                   // <- that's it
public ActionResult<TodoItem> GetById(Guid id) => ...
```

```csharp
app.MapGet("/customers/{id}", (int id) => ...)
   .McpTool();                              // <- same thing for Minimal APIs
```

## Setup

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddNabuMcp(options =>
{
    options.ServerName = "my-api";
    options.RequireAuthorization = true;                    // protect the MCP endpoint itself
    options.ToolVisibility = McpToolVisibility.Authorized;  // advertise only what the caller may invoke
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseNabuMcp();          // serves MCP at /mcp
app.MapControllers();
app.Run();
```

## Talk to it

```bash
curl -X POST http://localhost:5000/mcp \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

Tool descriptions come from your XML docs; input schemas are generated from your CLR types, data
annotations and nullability. `[Authorize]`, policies and roles are enforced per call — the same action
that returns 403 over HTTP returns an `isError` tool result over MCP for the same caller, and
`tools/list` can show each caller only the tools it is actually allowed to invoke.

## Why not just reflect over controllers?

Direct method invocation quietly drops everything that makes an action *safe*: `[Authorize]` is enforced
by middleware and filters, `ModelState` is populated by model binding, rate limiting and exception
handling live outside the method. Nabu instead captures the app's `RequestDelegate` at startup and pushes
a synthetic `HttpContext` — carrying the caller's identity and forwarded credentials — through the full
pipeline for every tool call.

## Highlights

- **One action, several tools** — apply `[McpTool]` repeatedly with different names, parameter subsets and pinned constants.
- **Per-caller tool visibility** — `tools/list` evaluated with your own authorization policies.
- **Protected headers** — a model-supplied argument can never override `Authorization`, `Cookie` or proxy headers.
- **Official SDK adapter** — `Nabu.Mcp.ModelContextProtocol` serves the same tools through the official MCP C# SDK.
- **Broad targets** — netstandard2.0 (ASP.NET Core 2.1+), net8.0, net10.0. MIT licensed.

📚 Full documentation: https://hovik-aghajanyan.github.io/nabu.net/
