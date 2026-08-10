# Nabu.NET

[![CI](https://github.com/hovik-aghajanyan/nabu.net/actions/workflows/ci.yml/badge.svg)](https://github.com/hovik-aghajanyan/nabu.net/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Nabu.Mcp.AspNetCore.svg)](https://www.nuget.org/packages/Nabu.Mcp.AspNetCore)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Expose the ASP.NET Core Web API you already have as MCP tools by adding one attribute.**

`Nabu.Mcp.AspNetCore` turns existing controller actions into [Model Context Protocol](https://modelcontextprotocol.io)
tools. It does not ask you to re-declare your endpoints, duplicate validation, or re-implement your
security model. A tool call is replayed as a real HTTP request through **your application's own
pipeline**, so authentication, authorization policies, action filters, model binding, model validation,
exception handlers and every other piece of middleware keep working exactly as they do today.

```csharp
[HttpGet("{id:guid}")]
[McpTool]                                   // <- the entire integration
public ActionResult<TodoItem> GetById(Guid id) => ...
```

---

## Contents

- [Why replay the pipeline](#why-replay-the-pipeline)
- [Getting started](#getting-started)
- [Attributes](#attributes)
- [One action, several tools](#one-action-several-tools)
- [How arguments are mapped](#how-arguments-are-mapped)
- [Schema generation](#schema-generation)
- [Authentication and authorization](#authentication-and-authorization)
- [Configuration](#configuration)
- [Protocol support](#protocol-support)
- [Target frameworks](#target-frameworks)
- [Repository layout](#repository-layout)
- [Building and testing](#building-and-testing)
- [Releasing](#releasing)
- [Limitations](#limitations)

---

## Why replay the pipeline

The obvious way to build this is to reflect over controllers and invoke the action methods directly.
That approach quietly drops everything that makes an ASP.NET Core action *safe*: `[Authorize]` is
enforced by middleware and filters, not by the method body; `ModelState` is populated by model binding;
`[ServiceFilter]`, rate limiting, tenant resolution and exception handling all live outside the method.

Nabu takes the other route. During startup it installs an `IStartupFilter` that captures the fully
built `RequestDelegate` at the very front of the pipeline. To run a tool it constructs a synthetic
`HttpContext` for the target route - carrying the caller's identity, forwarded credentials, a fresh DI
scope and a JSON body built from the tool arguments - and pushes it through that captured pipeline.

The action cannot tell the difference between a tool call and a request from a browser. That is the
entire design goal, and the test suite asserts it: the same `[Authorize(Policy = "AdminOnly")]` that
returns 403 over HTTP returns an `isError` tool result over MCP, for the same caller.

## Getting started

### 1. Reference the library

```xml
<ProjectReference Include="path/to/src/Nabu.Mcp.AspNetCore/Nabu.Mcp.AspNetCore.csproj" />
```

### 2. Register and mount it

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddAuthentication(/* ... */);
builder.Services.AddAuthorization(/* ... */);

builder.Services.AddNabuMcp(options =>
{
    options.ServerName = "my-api";
    options.RequireAuthorization = true;   // protect the MCP endpoint itself
});

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.UseNabuMcp();      // mount after authentication so the endpoint sees the caller
app.MapControllers();

app.Run();
```

`UseNabuMcp()` serves the MCP endpoint at `/mcp` by default. Where you place it only affects the MCP
endpoint itself - tool calls always traverse the whole pipeline from the top, regardless of position.

### 3. Mark the actions you want to publish

```csharp
[ApiController]
[Route("api/todos")]
[Authorize]
public class TodosController : ControllerBase
{
    /// <summary>Lists the todo items belonging to the signed-in user.</summary>
    /// <param name="search">Case-insensitive substring matched against the title and notes.</param>
    [HttpGet]
    [McpTool]
    public ActionResult<TodoPage> List([FromQuery] string? search, [FromQuery] int page = 0) => ...
}
```

That is the whole setup. The XML `<summary>` becomes the tool description and the `<param>` text
becomes the argument descriptions, so a well-documented API produces a well-described tool set with no
extra work. (Set `<GenerateDocumentationFile>true</GenerateDocumentationFile>` to enable it.)

### 4. Talk to it

```bash
curl -X POST http://localhost:5000/mcp \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

```json
{
  "name": "todos_get_by_id",
  "title": "Get By Id",
  "description": "Fetches a single todo item by its identifier.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "id": { "type": "string", "format": "uuid", "description": "Identifier of the item." }
    },
    "required": ["id"]
  },
  "annotations": {
    "readOnlyHint": true, "destructiveHint": false, "idempotentHint": true, "openWorldHint": false
  }
}
```

## Attributes

| Attribute | Target | Purpose |
|---|---|---|
| `[McpTool]` | action | Publishes the action as a tool. Repeatable - see [One action, several tools](#one-action-several-tools). |
| `[McpTool]` | controller | Publishes every action on the controller. |
| `[McpIgnore]` | controller, action, parameter, property | Excludes it. Always wins. |
| `[McpParameter]` | parameter, property | Overrides the name, description, requiredness or example of one input. |

`[McpTool]` also accepts `Name`, `Title`, `Description`, `Enabled`, `RequiresAuthorization`, and the
four MCP behaviour hints `ReadOnly`, `Destructive`, `Idempotent` and `OpenWorld`. The hints default to
the HTTP semantics of the verb - GET is read-only and idempotent, DELETE and PUT are destructive - and
any hint you set explicitly overrides that default. `RequiresAuthorization` overrides what Nabu infers
about the action's authorization when it tailors the tool list to the caller - see
[Advertising tools per caller](#advertising-tools-per-caller).

```csharp
[HttpPost("{id:guid}/publish")]
[McpTool(
    Name = "publish_article",
    Description = "Publishes a draft article so it becomes visible to readers.",
    Idempotent = false,
    Destructive = true)]
public IActionResult Publish(Guid id) => ...
```

## One action, several tools

A single endpoint is often the wrong shape for a model. `GET /api/todos` with five optional filters is
easy for a client that already knows what it wants and awkward for a model that has to guess. Apply
`[McpTool]` more than once and the same action is published as several tools, each with its own name,
description and parameter set - without adding controller actions, and with the same pipeline replay
behind every one of them.

```csharp
[HttpGet]
[McpTool]                                                       // the full endpoint, unchanged
[McpTool("todos_list_open",
    Title = "List open todos",
    Description = "Lists the todo items that are still open.",
    ExcludeParameters = new[] { "search", "priority" },
    ConstantParameters = new[] { "isCompleted=false" })]
[McpTool("todos_search",
    Title = "Search todos",
    Description = "Searches the signed-in user's todo items by title and notes.",
    IncludeParameters = new[] { "search", "page", "pageSize" },
    RequiredParameters = new[] { "search" })]
public ActionResult<TodoPage> List(
    [FromQuery] bool? isCompleted,
    [FromQuery] TodoPriority? priority,
    [FromQuery] string? search,
    [FromQuery] int page = 0,
    [FromQuery] int pageSize = 20) => ...
```

`tools/list` now advertises three tools over one action: `todos_list` takes all five filters,
`todos_list_open` takes only `page` and `pageSize` and can never return completed items, and
`todos_search` takes a mandatory `search` plus paging.

| Property | Effect |
|---|---|
| `IncludeParameters` | Whitelist. Only these inputs are exposed; everything else is left unset. |
| `ExcludeParameters` | Hides inputs. They are left unset, so the action's own defaults apply. |
| `ConstantParameters` | `name=value` pairs. The input disappears from the schema and the value is always sent. |
| `RequiredParameters` | Marks inputs required for this tool even if the action treats them as optional. |
| `OptionalParameters` | The reverse. Route tokens the URL cannot be built without stay required. |

Notes:

- Names match either the tool input name (camelCase, as it appears in the schema) or the underlying
  binding name, case-insensitively. A name that matches nothing is logged as a warning.
- Constant values are converted to the parameter's CLR type: `pageSize=100` becomes a JSON number,
  `isCompleted=false` a JSON boolean, a complex parameter accepts a JSON literal such as
  `tags=["docs","ops"]`, and everything else is sent as a string. On a non-string parameter an empty
  value or `null` means "send nothing".
- A constant always wins over an argument that happens to target the same place, so a pinned value
  cannot be talked out of by the model.
- Route tokens can be pinned too, which is how an endpoint collapses into a zero-argument tool:

  ```csharp
  [HttpGet("{city}")]
  [McpTool]
  [McpTool("weather_get_yerevan_week", ConstantParameters = new[] { "city=Yerevan", "days=7" })]
  public ActionResult<IEnumerable<Forecast>> GetForecast(string city, [FromQuery] int days = 3) => ...
  ```

  Hiding a route token *without* pinning it would produce a tool whose URL cannot be built, so Nabu
  logs a warning and skips that variant rather than publishing something uncallable.
- Give every extra variant an explicit `Name`. Variants without one fall back to the generated
  `controller_action` name and collide, and all but the first end up with a `_2`, `_3`, ... suffix.
- Variants declared on an action replace a controller-wide `[McpTool]` rather than adding to it.

## How arguments are mapped

Nabu reads MVC's own binding metadata, so it maps arguments the same way your API already binds them.

| Binding source | Becomes |
|---|---|
| `[FromRoute]` / route template token | A URL segment, URL-encoded. |
| `[FromQuery]` | A query-string entry. Arrays repeat the key; objects use `key.property`. |
| `[FromBody]` | The JSON request body. |
| `[FromHeader]` | A request header (opt in with `ExposeHeaderParameters`). |
| `[FromServices]`, `CancellationToken`, `HttpContext` | Skipped - resolved by the framework. |

When no explicit attribute is present, Nabu infers the source exactly as `[ApiController]` does: route
tokens first, then body for complex types on POST/PUT/PATCH, then query string.

**Body flattening.** A single complex `[FromBody]` parameter is flattened into the top level of the
tool schema, so a model fills one flat object instead of a nested wrapper:

```csharp
public ActionResult<TodoItem> Create([FromBody] CreateTodoRequest request)
```
```jsonc
// arguments: {"title": "...", "priority": "High", "tags": ["a"]}   not  {"request": {...}}
```

Set `FlattenBodyParameter = false` to keep the wrapper. Types that are not objects - a `[FromBody]
int[]`, for example - are always sent as the whole body under their parameter name.

## Schema generation

Input schemas are generated from the CLR types and honour:

- primitives, `Guid`, `DateTime`/`DateTimeOffset`/`DateOnly`/`TimeOnly`/`TimeSpan`, `Uri`, `byte[]`
- `Nullable<T>` and nullable reference types (`string?` is optional, `string` is required)
- collections, `string`-keyed dictionaries, nested models, with cycle and depth protection
- `[Required]`, `[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[RegularExpression]`,
  `[EmailAddress]`, `[Url]`, `[DefaultValue]`, `[Description]`, `[Display]`
- `[JsonPropertyName]`, `[JsonIgnore]`
- XML documentation `<summary>` on models and properties

**Enums** are always described to the model by name, because names are what a model can reason about.
If your API serializes enums as numbers - the default for both System.Text.Json and Newtonsoft.Json -
Nabu detects that and converts the names back to their numeric values while building the request body,
including inside nested objects and arrays. Nothing to configure; override it with
`StringEnumsInRequestBody` if the detection is ever wrong.

## Authentication and authorization

There are two independent layers, and both are enforced.

**The MCP endpoint.** `RequireAuthorization = true` makes `/mcp` itself require an authenticated
caller, optionally against a named policy (`AuthorizationPolicy`) and specific schemes
(`AuthenticationSchemes`). Unauthenticated callers get a challenge; authenticated ones without the
policy get a forbid.

**Each tool call.** Because the synthetic request traverses the real pipeline, the target action's own
`[Authorize]`, policies, roles, claims and custom filters run untouched. Nabu does not interpret,
cache or shortcut them.

Identity reaches the action two ways, which reinforce each other:

1. Credentials-bearing headers (`Authorization`, `Cookie`, tracing headers, and anything you add to
   `ForwardedHeaders` / `ForwardedHeaderPrefixes`) are copied onto the synthetic request, so your
   authentication middleware re-authenticates it normally.
2. The `ClaimsPrincipal` established for the MCP request is seeded onto the synthetic context, so
   schemes whose credentials cannot be replayed from headers alone still work. Authentication
   middleware overwrites it whenever the forwarded credentials authenticate successfully. Disable with
   `PropagateUser = false`.

Hop-by-hop and content headers (`Content-Length`, `Transfer-Encoding`, `Accept-Encoding`, `Host`, ...)
are never forwarded; they are rebuilt for the synthetic request.

### Advertising tools per caller

By default every discovered tool is advertised to everyone, and a caller that invokes one it is not
allowed to use gets the action's own 401 or 403 back as a tool error. That is safe, but it hands the
model a menu it cannot order from - and a client added before anyone has signed in sees the whole
catalogue.

`ToolVisibility` tailors `tools/list` to whoever is asking:

```csharp
options.ToolVisibility = McpToolVisibility.Authorized;
```

| Value | `tools/list` contains |
|---|---|
| `All` (default) | Every discovered tool, whoever is asking. |
| `Authenticated` | Tools whose actions need authorization, only once the caller is authenticated. |
| `Authorized` | Tools whose actions need authorization, only once the caller satisfies their policies. |

Nabu reads the requirement during discovery, from the `[Authorize]` and `[AllowAnonymous]` metadata of
the action, its controller and the filter collection - `[AllowAnonymous]` wins, exactly as it does in
MVC - and evaluates it against the caller with the application's own `IAuthorizationPolicyProvider` and
`IAuthorizationService`. An unauthenticated caller is therefore shown only the tools that need no
authorization; the rest appear when it lists the tools again with credentials.

This decides what is *advertised*, never what is allowed. A hidden tool that gets called anyway is
still replayed through the pipeline and still refused by the action, so filtering can never be the only
thing standing between a caller and an endpoint. When the requirement cannot be worked out - a custom
filter Nabu cannot see, a missing authorization service - the tool stays visible, because a spurious 403
is a better failure than a capability that silently disappeared. Two escape hatches exist for the cases
Nabu reads wrongly:

```csharp
[McpTool(RequiresAuthorization = true)]     // treat as protected regardless of what the metadata says
```

```csharp
// or take the decision over entirely
services.AddSingleton<IMcpToolAuthorizationEvaluator, MyEvaluator>();
```

### Adding a client before it has credentials

`RequireAuthorization = true` makes `/mcp` itself reject anonymous callers, which is what triggers the
OAuth flow in MCP clients that support one - but it also means a client cannot so much as initialize
until credentials exist. `AnonymousAccess` opens a narrow door in that gate:

| Value | An unauthorized caller may |
|---|---|
| `None` (default) | Nothing. Every request is challenged. |
| `Discovery` | `initialize`, `ping` and the listing methods. `tools/call` is still challenged. |
| `AnonymousTools` | The above, plus `tools/call` for tools whose actions need no authorization. |

```csharp
options.RequireAuthorization = true;
options.AnonymousAccess = McpAnonymousAccess.AnonymousTools;
options.ToolVisibility = McpToolVisibility.Authorized;
```

Now a client added without credentials connects, lists the public tools and can use them; everything
else is a 401, which is exactly the signal a client needs to start authenticating; and once it does, the
next `tools/list` returns the full set it is entitled to. Pair the two options as above - `AnonymousAccess`
on its own would advertise tools the anonymous caller cannot call.

Because the server is stateless it cannot push `notifications/tools/list_changed`, so a client that
caches the tool list should re-list after authenticating.

> Because the MCP endpoint hands one authenticated caller the ability to invoke every published action,
> publish deliberately. `[McpTool]` is opt-in for exactly this reason, and `[McpIgnore]` lets you keep
> an action reachable over HTTP while hiding it from MCP.

## Configuration

All options live on `NabuMcpOptions`:

| Option | Default | Meaning |
|---|---|---|
| `Path` | `/mcp` | Endpoint path. |
| `ServerName`, `ServerVersion`, `Instructions` | entry assembly | Reported during `initialize`. |
| `ExposeAllActions` | `false` | Publish every action, not just annotated ones. `[McpIgnore]` still wins. |
| `RequireAuthorization`, `AuthorizationPolicy`, `AuthenticationSchemes` | off | Protects the MCP endpoint. |
| `ToolVisibility` | `All` | Advertise every tool, or only the ones the caller is authenticated / authorized for. |
| `AnonymousAccess` | `None` | How much of a protected endpoint an unauthorized caller may reach. |
| `ToolNameFactory` | `controller_action` snake_case | Builds tool names. |
| `ToolFilter` | none | Last-chance predicate to drop discovered tools. |
| `FlattenBodyParameter` | `true` | Lift `[FromBody]` model properties to the top level. |
| `ExposeHeaderParameters` | `false` | Publish `[FromHeader]` parameters as tool inputs. |
| `ForwardedHeaders`, `ForwardedHeaderPrefixes` | credentials + tracing | Headers copied onto tool requests. |
| `PropagateUser` | `true` | Seed the caller's principal onto the synthetic request. |
| `MaxResponseBytes` | 1 MiB | Response bodies above this are truncated and flagged. |
| `IncludeStructuredContent` | `true` | Emit JSON responses as MCP `structuredContent`. |
| `TreatErrorStatusAsToolError` | `true` | Map HTTP >= 400 to `isError: true`. |
| `MaxSchemaDepth` | `8` | Nesting limit for generated schemas. |
| `UseXmlDocumentation` | `true` | Read `<summary>`/`<param>` from the XML docs file. |
| `StringEnumsInRequestBody` | auto-detect | Whether the API accepts enum names in JSON bodies. |

## Protocol support

Streamable HTTP transport, JSON-RPC 2.0, protocol revisions `2025-06-18`, `2025-03-26` and
`2024-11-05` (negotiated at `initialize`).

| Method | Behaviour |
|---|---|
| `initialize` | Negotiates the version, advertises the `tools` capability, returns a session id. |
| `tools/list` | Every discovered tool with its schema and annotations. |
| `tools/call` | Replays the action; returns text content, `structuredContent`, and `isError`. |
| `ping` | Answered with an empty result. |
| `notifications/*` | Accepted with `202` and no body. |
| `resources/list`, `resources/templates/list`, `prompts/list` | Answered as empty for client compatibility. |

`POST` returns `application/json`, or a single server-sent event when the client accepts only
`text/event-stream`. Batched arrays are supported. `DELETE` ends a session (the server is stateless, so
this is a no-op). `GET` returns `405`.

Failures are reported at the right layer: a bad tool name or a missing required argument is a JSON-RPC
error (`-32602`), while an HTTP error from the action - 400 from validation, 403 from a policy, 404
from the action - is a successful JSON-RPC response carrying `isError: true`, which is what lets a
model read and react to it.

## Target frameworks

The library multi-targets `netstandard2.0`, `net6.0` and `net8.0`:

- **netstandard2.0** builds against the ASP.NET Core 2.x packages, so ASP.NET Core 2.1/2.2 apps can use it.
- **net6.0** and **net8.0** bind to the shared framework, so modern apps pull in no legacy assemblies.

Version-specific APIs are handled at runtime rather than by feature-flagging behaviour: the HTTP verb
is read through `IActionHttpMethodProvider`, which has been stable since 1.0, and the enum
representation is detected reflectively so the same code path serves System.Text.Json and
Newtonsoft.Json.

## Repository layout

```
src/Nabu.Mcp.AspNetCore/     the framework
samples/Nabu.Sample.TodoApi/ a JWT-secured todo API wired up with Nabu
tests/Nabu.Mcp.AspNetCore.Tests/  unit and integration tests
.github/workflows/           CI on every push and PR, NuGet publishing on every v* tag
```

The sample is a working API with JWT bearer authentication, a role-based policy, model validation and
XML documentation. Two accounts exist, both with the password `password`: `alice` (user) and `root`
(user + admin).

```bash
dotnet run --project samples/Nabu.Sample.TodoApi
# then POST JSON-RPC to http://localhost:5000/mcp
```

## Building and testing

```bash
dotnet build          # netstandard2.0 + net6.0 + net8.0
dotnet test           # 180 tests
```

The suite covers route template parsing, tool naming, JSON schema generation, argument binding,
constant parsing and enum coercion as units, and drives the real sample application in memory for
discovery, invocation, authorization and protocol behaviour - including that a tool call for one user
never returns another user's data, and that an admin-only action stays admin-only when reached over
MCP.

Every push to `main` and every pull request runs the same build, test and pack on GitHub Actions
(`.github/workflows/ci.yml`).

## Releasing

Publishing to [nuget.org](https://www.nuget.org/packages/Nabu.Mcp.AspNetCore) is driven by tags. The
tag is the single source of truth for the package version, so `VersionPrefix` in
`Directory.Build.props` never has to be kept in sync by hand.

```bash
git tag v1.2.3
git push origin v1.2.3
```

`.github/workflows/release.yml` then restores, builds, tests, packs `Nabu.Mcp.AspNetCore` at `1.2.3`,
pushes the `.nupkg` and its `.snupkg` symbol package to nuget.org, and opens a GitHub release with
generated notes and the packages attached. A tag containing a hyphen (`v1.2.3-rc.1`) is published as a
prerelease.

One-time setup: the workflow authenticates with [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
so there is no API key to store. On nuget.org (*username → Trusted Publishing*) create a policy with
Repository Owner `hovik-aghajanyan`, Repository `Nabu.NET`, Workflow File `release.yml`, and no
environment. A policy created before the first publish must be used (or re-activated) within 7 days.

The workflow can also be started manually from the Actions tab with an explicit version, and has a
`dry_run` option that builds and packs without pushing anything.

## Limitations

- Actions binding `IFormFile` or form data are skipped, with a warning; tools cannot supply files.
- Attribute routing is what discovery is built around. Conventionally routed actions fall back to a
  `{controller}/{action}` path with the remaining arguments in the query string.
- The server is stateless: no server-initiated notifications, no `listChanged`, no resumable streams.
- Overloaded actions that generate the same tool name are disambiguated with a numeric suffix and a
  warning; give them explicit `[McpTool(Name = "...")]` names instead.

## License

MIT. See [LICENSE](LICENSE).
