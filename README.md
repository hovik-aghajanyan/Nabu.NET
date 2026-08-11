# Nabu.NET

[![CI](https://github.com/hovik-aghajanyan/nabu.net/actions/workflows/ci.yml/badge.svg)](https://github.com/hovik-aghajanyan/nabu.net/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Nabu.Mcp.AspNetCore.svg)](https://www.nuget.org/packages/Nabu.Mcp.AspNetCore)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

**Turn the ASP.NET Core Web API you already have into an MCP server - without rewriting your API.**

One attribute. Same endpoint. Same authorization. Same validation. Same middleware.

`Nabu.Mcp.AspNetCore` turns existing controller actions and Minimal API route handlers into
[Model Context Protocol](https://modelcontextprotocol.io) tools. It does not ask you to re-declare your endpoints, duplicate validation, or re-implement your
security model. A tool call is replayed as a real HTTP request through **your application's own
pipeline**, so authentication, authorization policies, action filters, model binding, model validation,
exception handlers and every other piece of middleware keep working exactly as they do today. Tool
visibility can even be evaluated with your application's own authorization policies, so `tools/list`
shows each caller only the tools it is actually allowed to invoke - see
[Advertising tools per caller](#advertising-tools-per-caller).

```csharp
[HttpGet("{id:guid}")]
[McpTool]                                   // <- the entire integration
public ActionResult<TodoItem> GetById(Guid id) => ...
```

```csharp
app.MapGet("/customers/{id}", (int id) => ...)
   .McpTool();                              // <- same thing for Minimal APIs
```

---

## Contents

- [Why replay the pipeline](#why-replay-the-pipeline)
- [Getting started](#getting-started)
- [Attributes](#attributes)
- [Minimal APIs](#minimal-apis)
- [One action, several tools](#one-action-several-tools)
- [How arguments are mapped](#how-arguments-are-mapped)
- [Schema generation](#schema-generation)
- [Authentication and authorization](#authentication-and-authorization)
- [Configuration](#configuration)
- [Protocol support](#protocol-support)
- [Target frameworks](#target-frameworks)
- [Repository layout](#repository-layout)
- [Trying it with MCP Inspector](#trying-it-with-mcp-inspector)
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

A tool call therefore runs through the same ASP.NET Core application pipeline as a client-issued
request and preserves standard HTTP application semantics: request and response, connection
information, request services, cancellation and the caller's identity all behave as they do for a
real request. The test suite asserts it: the same `[Authorize(Policy = "AdminOnly")]` that returns
403 over HTTP returns an `isError` tool result over MCP, for the same caller.

What the synthetic request carries is the HTTP *application* surface, not the server transport.
Middleware that depends on server-level features - client certificates and other TLS features,
HTTP/2-specific server features, WebSockets, response upgrade, raw transport access - will find them
absent, exactly as it would behind some reverse proxies. See [Limitations](#limitations).

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

`UseNabuMcp()` serves the MCP endpoint at `/mcp` by default. The route can be changed either
through `options.Path` or directly at the mount - `app.UseNabuMcp("/agent/mcp")` - with the
argument winning when both are set. Where you place it only affects the MCP endpoint itself -
tool calls always traverse the whole pipeline from the top, regardless of position.

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

For Minimal APIs the same declarations are made with the `.McpTool()` / `.McpIgnore()` endpoint
conventions (or the same attributes on the handler delegate) - see [Minimal APIs](#minimal-apis).

`[McpTool]` also accepts `Name`, `Title`, `Description`, `Enabled`, and the four MCP behaviour hints
`ReadOnly`, `Destructive`, `Idempotent` and `OpenWorld`. The hints default to the HTTP semantics of the
verb - GET is read-only and idempotent, DELETE and PUT are destructive - and any hint you set
explicitly overrides that default.

```csharp
[HttpPost("{id:guid}/publish")]
[McpTool(
    Name = "publish_article",
    Description = "Publishes a draft article so it becomes visible to readers.",
    Idempotent = false,
    Destructive = true)]
public IActionResult Publish(Guid id) => ...
```

## Minimal APIs

Route handlers publish the same way controller actions do, through an endpoint convention instead of
an attribute:

```csharp
app.MapGet("/customers/{id}", (int id, ICustomerService customers) => customers.Get(id))
   .McpTool();

app.MapPost("/orders", (CreateOrder order) => ...)
   .McpTool("orders_create", "Places a new order.");
```

Everything works exactly as it does for controllers, because the machinery is the same: a tool call
is replayed as a synthetic HTTP request through the whole pipeline, so `RequireAuthorization()`,
`[Authorize]`, endpoint filters, rate limiting and validation all keep running, and
`ToolVisibility` reads the endpoint's own authorization metadata when tailoring `tools/list` to the
caller. An application without any controllers at all - `AddNabuMcp()` plus `UseNabuMcp()` on a
plain `WebApplication` - is fully supported.

Inputs are inferred with Minimal API binding rules, not MVC's:

| Handler parameter | Becomes |
|---|---|
| Route token match, `[FromRoute]` | A URL segment. |
| `string`, primitives, parsables, and collections of them; `[FromQuery]` | A query-string entry. |
| Complex types (and collections of them), `[FromBody]` | The JSON request body, [flattened](#how-arguments-are-mapped) like `[FromBody]`. |
| `[FromHeader]` | A request header (opt in with `ExposeHeaderParameters`). |
| Services (per `IServiceProviderIsService`), `[FromServices]`, `[FromKeyedServices]` | Skipped - resolved by the framework. |
| `HttpContext`, `CancellationToken`, `ClaimsPrincipal`, `Stream`, `PipeReader`, `BindAsync` types | Skipped - materialized from the request itself. |

One real difference between the stacks is preserved rather than papered over: Minimal APIs answer
400 when a non-nullable parameter without a default value is missing, so such a parameter is
`required` in the tool schema - where MVC model binding would have silently used the type's default.

The other `[McpTool]` surfaces work here too:

- `.McpTool(tool => { ... })` exposes the full attribute surface - `IncludeParameters`,
  `ConstantParameters`, behaviour hints - and calling `.McpTool(...)` repeatedly publishes
  [several tools over one endpoint](#one-action-several-tools).
- An attribute on the handler itself is equivalent to the extension:
  `app.MapGet("/ping", [McpTool("ping")] () => ...)`.
- `.McpIgnore()` (or `[McpIgnore]` on the handler) keeps an endpoint out of discovery, which
  matters when `ExposeAllActions` is on - that option publishes every delegate route handler just
  as it publishes every controller action, and `ExcludeFromDescription()` is respected the way
  `[ApiExplorerSettings(IgnoreApi = true)]` is for controllers.

Without an explicit name, tools are named from the endpoint name (`.WithName(...)`), the handler's
method name when it is a real method rather than a lambda, or the verb and route -
`GET /customers/{id}` becomes `customers_get_by_id`. `NabuMcpOptions.ToolNameFactory` sees these
through the same `McpToolNamingContext` it sees controllers through.

Current limitations: `[AsParameters]` models and form/`IFormFile` binding are not supported - such
endpoints are skipped with a warning. Minimal API discovery needs endpoint routing, so it exists on
the modern targets only, not on the netstandard2.0 asset for ASP.NET Core 2.x.

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

**Protected headers.** A model-supplied argument can never override the headers that carry
credentials or proxy metadata. Even with `ExposeHeaderParameters` on, a tool argument that binds to a
header in `ProtectedHeaders` - by default `Authorization`, `Cookie`, `Host`, `X-Forwarded-For`,
`X-Forwarded-Proto` and `X-Forwarded-Host` - is dropped with a warning, and the value forwarded from
the MCP caller stays in place. Constants pinned by the developer through `ConstantParameters` are
exempt, because they are developer input rather than model input. Removing a name from the set is the
explicit opt-in:

```csharp
options.ProtectedHeaders.Remove("Authorization");   // only if you really mean it
```

### Recommended production configuration

The defaults are tuned for a first run: the MCP endpoint is anonymous and every discovered tool is
advertised, while each invocation is still authorized by the application pipeline. That is never an
authorization bypass - a caller can at most see the names and schemas of tools it cannot invoke - but
for production deployments, lock the endpoint down and tailor the catalogue to the caller:

```csharp
options.RequireAuthorization = true;                    // the endpoint itself challenges anonymous callers
options.ToolVisibility = McpToolVisibility.Authorized;  // advertise only what the caller may actually invoke
```

The sections below describe both knobs in detail.

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
the action, its controller and the filter collection, and resolves it the way `AuthorizationMiddleware`
would for a real request:

- `[AllowAnonymous]` on the action or the controller wins outright, exactly as it does in MVC.
- `[Authorize]` - with a policy, roles, or schemes - is combined into a single policy, including a
  globally registered `AuthorizeFilter`.
- An action carrying **neither** is not assumed to be public: `AuthorizationOptions.FallbackPolicy`
  applies to it, so in a secure-by-default application every action is protected until an
  `[AllowAnonymous]` opts it out, and the tool list says the same.

The resolved policy is then evaluated against the caller with the application's own
`IAuthorizationPolicyProvider` and `IAuthorizationService`. An unauthenticated caller is therefore
shown only the tools that need no authorization; the rest appear when it lists the tools again with
credentials.

Nothing MCP-specific is involved: the attributes that already secure the API are the ones that decide,
so an action opts out of its controller's `[Authorize]` the same way it always has.

```csharp
[ApiController]
[Route("api/todos")]
[Authorize]                                 // the controller is protected...
public class TodosController : ControllerBase
{
    /// <summary>Lists the priority levels a todo item can be given.</summary>
    [HttpGet("priorities")]
    [AllowAnonymous]                        // ...and this one action is not
    [McpTool]
    public ActionResult<IEnumerable<string>> GetPriorities() => ...
}
```

`todos_get_priorities` is advertised to, and callable by, a caller holding no token; every other todo
tool waits until it signs in.

This decides what is *advertised*, never what is allowed. A hidden tool that gets called anyway is
still replayed through the pipeline and still refused by the action, so filtering can never be the only
thing standing between a caller and an endpoint. When the requirement cannot be worked out - a custom
filter Nabu cannot see, a missing authorization service - the tool stays visible, because a spurious 403
is a better failure than a capability that silently disappeared. When authorization depends on something
no attribute expresses, take the decision over entirely:

```csharp
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

The door is only open to callers that hold no credentials. One that presents a token which is *rejected*
is challenged as before, so an expired or malformed token is never quietly downgraded to the anonymous
tool list.

> If the application sets `AuthorizationOptions.FallbackPolicy`, mount `UseNabuMcp()` **before**
> `UseAuthorization()`. A fallback policy applies to every request that matches no endpoint, and the MCP
> endpoint is middleware rather than an endpoint, so authorization would otherwise challenge it before
> Nabu saw it - `AnonymousAccess` included. Tool calls are unaffected either way: they traverse the
> whole pipeline and meet the fallback policy at the action.

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
| `ServerName`, `ServerVersion`, `Instructions` | entry assembly | Reported during `initialize` / `server/discover`. |
| `CacheTtlMilliseconds` | `60000` | `ttlMs` freshness hint on cacheable `2026-07-28` results. |
| `ExposeAllActions` | `false` | Publish every action and route handler, not just annotated ones. `[McpIgnore]` still wins. |
| `RequireAuthorization`, `AuthorizationPolicy`, `AuthenticationSchemes` | off | Protects the MCP endpoint. |
| `ToolVisibility` | `All` | Advertise every tool, or only the ones the caller is authenticated / authorized for. |
| `AnonymousAccess` | `None` | How much of a protected endpoint an unauthorized caller may reach. |
| `ToolNameFactory` | `controller_action` snake_case | Builds tool names. |
| `ToolFilter` | none | Last-chance predicate to drop discovered tools. |
| `FlattenBodyParameter` | `true` | Lift `[FromBody]` model properties to the top level. |
| `ExposeHeaderParameters` | `false` | Publish `[FromHeader]` parameters as tool inputs. |
| `ForwardedHeaders`, `ForwardedHeaderPrefixes` | credentials + tracing | Headers copied onto tool requests. |
| `ProtectedHeaders` | credentials + proxy metadata | Headers a model-supplied argument may never override. |
| `PropagateUser` | `true` | Seed the caller's principal onto the synthetic request. |
| `MaxResponseBytes` | 1 MiB | Cap on the buffered response body; bounds memory, larger bodies are truncated and flagged. |
| `IncludeStructuredContent` | `true` | Emit JSON responses as MCP `structuredContent`. |
| `TreatErrorStatusAsToolError` | `true` | Map HTTP >= 400 to `isError: true`. |
| `MaxSchemaDepth` | `8` | Nesting limit for generated schemas. |
| `UseXmlDocumentation` | `true` | Read `<summary>`/`<param>` from the XML docs file. |
| `StringEnumsInRequestBody` | auto-detect | Whether the API accepts enum names in JSON bodies. |

## Protocol support

Streamable HTTP transport, JSON-RPC 2.0. The server is dual-era: it speaks the stateless
revision `2026-07-28` (version and capabilities declared in `_meta` on every request, no
handshake, no sessions) and the legacy revisions `2025-06-18`, `2025-03-26` and `2024-11-05`
(negotiated at `initialize`). A request that declares a protocol version in `_meta` — or calls
`server/discover` — is served under `2026-07-28` semantics; everything else stays on the
legacy path, so existing clients are unaffected.

On the `2026-07-28` path the mirrored request headers (`MCP-Protocol-Version`, `Mcp-Method`,
`Mcp-Name`) are validated against the body (`-32020` on mismatch), an unsupported version is
answered with `-32022` and the supported list, results carry `resultType`, the server's
identity in `_meta`, and `ttlMs`/`cacheScope` on cacheable results, and no session id is ever
minted.

| Method | Behaviour |
|---|---|
| `server/discover` | (`2026-07-28`) Supported versions, capabilities, identity and instructions. |
| `initialize` | (legacy) Negotiates the version, advertises the `tools` capability, returns a session id. |
| `tools/list` | Every discovered tool with its schema and annotations. |
| `tools/call` | Replays the action; returns text content, `structuredContent`, and `isError`. |
| `ping` | (legacy) Answered with an empty result. |
| `notifications/*` | Accepted with `202` and no body. |
| `resources/list`, `resources/templates/list`, `prompts/list` | Answered as empty for client compatibility. |

`POST` returns `application/json`, or a single server-sent event when the client accepts only
`text/event-stream`. Batched arrays are supported on the legacy path. `DELETE` ends a legacy
session (the server is stateless, so this is a no-op). `GET` returns `405`.

Failures are reported at the right layer: a bad tool name or a missing required argument is a JSON-RPC
error (`-32602`), while an HTTP error from the action - 400 from validation, 403 from a policy, 404
from the action - is a successful JSON-RPC response carrying `isError: true`, which is what lets a
model read and react to it.

### Using the official MCP SDK as the protocol layer

Nabu's value is the ASP.NET Core bridge - controller discovery, schema generation,
authorization-aware visibility and pipeline replay - not the JSON-RPC plumbing. The protocol
layer is therefore replaceable. The `Nabu.Mcp.ModelContextProtocol` package serves Nabu's tools
through the official [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
instead of the built-in layer:

```csharp
services.AddNabuMcp(options => { ... })
        .UseOfficialMcpProtocol();

app.UseNabuMcp(); // now mounts the official SDK's Streamable HTTP endpoint at the same path
```

The SDK owns the wire protocol - transport, protocol revisions, sessions - and Nabu answers
`tools/list` and `tools/call` behind it, with the same visibility rules and pipeline replay as
the built-in layer. The transport defaults to the SDK's stateless mode, matching Nabu's design;
pass a configuration delegate to change that or any other transport option:

```csharp
services.AddNabuMcp().UseOfficialMcpProtocol(transport => transport.Stateless = false);
```

Choose the built-in layer for netstandard2.0/ASP.NET Core 2.x compatibility and zero extra
dependencies; choose the official layer (net8.0+) to track the protocol at the SDK's pace and
pick up its transport features. `RequireAuthorization` is honoured on the mapped endpoint; the
partial `AnonymousAccess` modes leave the endpoint anonymous and rely on the replayed pipeline
to authorize each call.

`samples/Nabu.Sample.OfficialSdk` is a complete runnable example: a small book-catalog API
published through the official SDK on a custom route (`app.UseNabuMcp("/books/mcp")`). It carries
the same JWT setup and per-caller tool exposure as the Todo sample - `books_search` and
`books_get` are anonymous, `books_add` requires a signed-in caller, and `books_remove` appears
only for an administrator - showing that the authorization story is identical on both protocol
layers.

## Target frameworks

The library multi-targets `netstandard2.0`, `net8.0` and `net10.0`:

- **netstandard2.0** builds against the ASP.NET Core 2.x packages, so ASP.NET Core 2.1/2.2 apps can use it.
- **net8.0** and **net10.0** bind to the shared framework, so modern apps pull in no legacy assemblies.

Version-specific APIs are handled at runtime rather than by feature-flagging behaviour: the HTTP verb
is read through `IActionHttpMethodProvider`, which has been stable since 1.0, and the enum
representation is detected reflectively so the same code path serves System.Text.Json and
Newtonsoft.Json.

## Repository layout

```
src/Nabu.Mcp.AspNetCore/            the framework
src/Nabu.Mcp.ModelContextProtocol/  optional adapter serving Nabu's tools through the official MCP C# SDK
samples/Nabu.Sample.TodoApi/        a JWT-secured todo API wired up with Nabu
samples/Nabu.Sample.OfficialSdk/    a book catalog served through UseOfficialMcpProtocol()
tests/Nabu.Mcp.AspNetCore.Tests/  unit and integration tests
.github/workflows/           CI on every push and PR, NuGet publishing on every v* tag
```

The sample is a working API with JWT bearer authentication, a role-based policy, model validation and
XML documentation. Two accounts exist, both with the password `password`: `alice` (user) and `root`
(user + admin).

It is wired up for per-caller tool visibility, so the ordinary authorization attributes are visible at
work. Connect without a token and `tools/list` returns the `weather_*` tools, because
`WeatherController` carries `[AllowAnonymous]`, plus `todos_get_priorities`, because that one action
carries `[AllowAnonymous]` even though its controller carries `[Authorize]`, plus `server_time_now`,
a Minimal API endpoint published with `.McpTool()` - and all of them can be called. The rest of the
`todos_*` tools are neither advertised nor callable until you sign in, and `todos_delete` appears
only for `root`, because it carries `[Authorize(Policy = "AdminOnly")]`.

```bash
dotnet run --project samples/Nabu.Sample.TodoApi
# then POST JSON-RPC to http://localhost:5000/mcp
```

## Trying it with MCP Inspector

`docker-compose.yml` runs both samples together with the official
[MCP Inspector](https://github.com/modelcontextprotocol/inspector), preconfigured to demonstrate the
per-caller tool exposure above on both protocol layers:

```bash
docker compose up --build
# then open http://localhost:6274?MCP_INSPECTOR_API_TOKEN=nabu-local-dev
```

A one-shot init container signs in as `alice` and `root` on both samples and writes an Inspector
config with three connections per sample, each to the same endpoint. For the Todo sample -
`todo-anonymous`, `todo-alice-user` and `todo-root-admin` - switching between them in the UI shows
the tool list grow from the 5 anonymous tools to alice's 11 to root's 12 (only root gets
`todos_delete`). For the book catalog served through the official SDK - `books-anonymous`,
`books-alice-user` and `books-root-admin` - the list grows from the 2 search tools to alice's 3
(`books_add`) to root's 4 (`books_remove`). The Todo API is published on
`http://localhost:5080/mcp` (5080 rather than 5000, which macOS AirPlay occupies) and the
book catalog on `http://localhost:5081/books/mcp`.

The same comparison from the terminal, via the Inspector CLI:

```bash
docker compose run --rm inspector --cli --config /shared/mcp-servers.json \
  --server todo-anonymous --method tools/list
docker compose run --rm inspector --cli --config /shared/mcp-servers.json \
  --server todo-root-admin --method tools/list
docker compose run --rm inspector --cli --config /shared/mcp-servers.json \
  --server books-root-admin --method tools/list
```

The demo tokens live for 24 hours; `docker compose up` again regenerates them.

## Building and testing

```bash
dotnet build          # netstandard2.0 + net8.0 + net10.0
dotnet test           # 243 tests
```

The suite covers route template parsing, tool naming, JSON schema generation, argument binding,
constant parsing and enum coercion as units, and drives the real sample application in memory for
discovery, invocation, authorization and protocol behaviour - including that a tool call for one user
never returns another user's data, that an admin-only action stays admin-only when reached over MCP,
and that a caller is advertised the tools its own credentials reach and no others.

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

- The synthetic request reproduces the HTTP application surface, not the server transport. Standard
  request/response semantics, connection information, request services, cancellation and identity are
  all present, but server-level features are not: TLS and client-certificate features, HTTP/2-specific
  server features, WebSockets, response upgrade, raw transport access and some IIS/Kestrel-specific
  features do not exist on a replayed request. Middleware that requires them will behave as it does
  when those features are absent.
- Actions binding `IFormFile` or form data are skipped, with a warning; tools cannot supply files.
  For Minimal APIs, `[AsParameters]` models are skipped the same way.
- Attribute routing is what discovery is built around. Conventionally routed actions fall back to a
  `{controller}/{action}` path with the remaining arguments in the query string.
- The server is stateless: no server-initiated notifications, no `listChanged`, no resumable streams.
- Overloaded actions that generate the same tool name are disambiguated with a numeric suffix and a
  warning; give them explicit `[McpTool(Name = "...")]` names instead.

## License

MIT. See [LICENSE](LICENSE).
