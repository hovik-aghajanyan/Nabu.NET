# Nabu.Mcp.AspNetCore.SignalR — SignalR hub methods as MCP tools

Date: 2026-08-20
Status: approved (invocation model, streaming, caller capture, merged endpoint approved by owner)

## Goal

A new sibling package, `Nabu.Mcp.AspNetCore.SignalR`, that publishes SignalR hub methods as MCP
tools with the same philosophy as the core package: **a tool call is replayed through the real
SignalR machinery**, not a reflection shortcut. Tools appear in the same `/mcp` catalogue as the
HTTP tools, on both protocol layers (built-in and official SDK), with the same attributes, schema
generation, naming, XML-doc descriptions and authorization-aware visibility.

## Approved decisions

1. **Invocation: synthetic in-process connection.** Each `tools/call` opens a synthetic SignalR
   connection through the app's own `HubConnectionHandler<THub>` over an in-memory duplex pipe —
   the same mechanics as SignalR's own `TestClient`. The real dispatcher runs: `IHubFilter`s,
   method-level `[Authorize]`, `OnConnectedAsync`/`OnDisconnectedAsync`, parameter binding via the
   hub protocol, streaming. Verified against dotnet/aspnetcore source.
2. **Streaming methods are exposed**, collected into an array bounded by a configurable cap, with a
   truncation flag — mirroring `MaxResponseBytes` semantics.
3. **Caller-directed messages are captured.** `Clients.Caller.SendAsync(...)` lands on the synthetic
   connection and is included in the tool result alongside the return value. Broadcasts to
   `Clients.All` / `Others` / groups go to real connected clients via the app's own machinery.
4. **Merged catalogue.** SignalR tools are served at the same `/mcp` endpoint. The core package
   gains two small, non-breaking extension points (below) that both protocol layers consume.

## Verified constraints (dotnet/aspnetcore source)

- `DefaultHubDispatcher` collects `[Authorize]` **only from the hub method** (`inherit: true`), and
  evaluates it via `IAuthorizationService` before invoking. **Hub-class-level `[Authorize]` is
  endpoint metadata**, enforced by `AuthorizationMiddleware` at the HTTP negotiate — a synthetic
  connection bypasses it. Therefore the invoker must evaluate the hub class requirement itself
  before connecting, using the same policy-combination rules the core already implements in
  `McpToolAuthorization` / `McpToolAuthorizationEvaluator`. Parity is asserted by tests.
- The TestClient pattern: `DuplexPipe`-style pair of `System.IO.Pipelines.Pipe`s, a
  `ConnectionContext` whose `User` carries the caller's `ClaimsPrincipal`
  (`IConnectionUserFeature`), handshake request first, then `InvocationMessage` /
  `CompletionMessage` / `StreamItemMessage` over the JSON hub protocol.
- `OnConnectedAsync`/`OnDisconnectedAsync` fire once per tool call. Inherent to faithful replay;
  documented, and the sample demonstrates it deliberately (presence announcements).

## Core extension points (Nabu.Mcp.AspNetCore) — non-breaking

1. **`IMcpToolSource`** — `IReadOnlyList<McpToolDescriptor> GetTools()`. `McpToolRegistry`
   aggregates all registered sources after its own HTTP discovery; name collisions get the existing
   suffix-and-warn treatment. Registered via DI (`IEnumerable<IMcpToolSource>`).
2. **Per-descriptor invoker dispatch.** `McpToolDescriptor` gains a source-agnostic constructor
   (no HTTP verb / route) and an `InvokerType` (`Type?`) property. `McpRequestHandler` and the
   official-SDK bridge resolve `InvokerType` from `RequestServices` when set, else use the default
   HTTP `IMcpToolInvoker`. Existing descriptors are untouched.

`McpToolAuthorization` on the descriptor already drives visibility; the SignalR source populates it
from hub class + method attributes, so `ToolVisibility`, `AnonymousAccess` etc. work unchanged.

## The SignalR package

`src/Nabu.Mcp.AspNetCore.SignalR`, TFMs `net10.0;net8.0` (mirrors `Nabu.Mcp.ModelContextProtocol`;
SignalR server types need the shared framework).

- **Discovery** (`SignalRHubToolSource : IMcpToolSource`): finds mapped hubs from
  `EndpointDataSource` (`HubMetadata` placed by `MapHub<THub>`), reflects public hub methods,
  honours `[McpTool]` (class or method, repeatable variants), `[McpIgnore]`, `[McpParameter]`,
  `ExposeAllHubMethods` option, reuses `JsonSchemaGenerator`, XML docs, behaviour-hint defaults
  (hub methods default to non-read-only, non-destructive) and `ToolNameFactory` naming —
  `ChatHub.SendMessage` → `chat_send_message`.
- **Invocation** (`SignalRHubToolInvoker : IMcpToolInvoker`): evaluate hub-class authorization
  against the caller; create pipe pair + synthetic `ConnectionContext` carrying the caller's
  principal and a fresh scope; run `HubConnectionHandler<THub>.OnConnectedAsync`; handshake with
  `JsonHubProtocol`; send the invocation with arguments coerced from the MCP `JsonObject` to the
  method's parameter order; collect stream items (capped), caller-directed sends (capped) and the
  completion; close the connection. HTTP >= 400 has no analog — a `CompletionMessage` with an error
  becomes `isError: true`.
- **Options** (`NabuMcpSignalROptions`): `ExposeAllHubMethods` (default false),
  `MaxStreamItems` (default 1000), `MaxCallerMessages` (default 100), invocation timeout.
- **Registration**: `services.AddNabuMcp(...).AddSignalRTools(options => ...)` — returns/extends
  the core builder pattern; requires `AddSignalR()` to have been called.

## Result shape

`structuredContent` mirrors the invocation: `{ "result": ..., "callerMessages": [{ "method": ...,
"arguments": [...] }...], "streamItems": [...], "truncated": bool? }` — collapsed to just the plain
return value when nothing else was captured, so simple hub methods read like simple HTTP tools.

## Sample

`samples/Nabu.Sample.ChatHub`: a JWT-secured chat application — same token setup and accounts as
the other samples (`alice`, `root`, password `password`). One `ChatHub` demonstrating: anonymous
method, `[Authorize]` method, admin-only method (`[Authorize(Policy = "AdminOnly")]`), a streaming
method, a `Clients.Caller` reply method, and a broadcast method — plus a static HTML page with a
real SignalR client so a browser visibly receives broadcasts triggered over MCP. Wired into
`docker-compose.yml` + Inspector config like the existing samples.

## Tests

New project `tests/Nabu.Mcp.AspNetCore.SignalR.Tests` mirroring the existing suites: discovery,
naming, schema, invocation, streaming caps, caller capture, and the signature auth-parity claims —
method-level policy 403 parity, hub-class-level `[Authorize]` enforced on the synthetic connection,
per-caller visibility of hub tools, one user never sees another's data.

## Docs, CI, release

- README: new "SignalR hubs" section + repo-layout/tests updates.
- `docs/`: new `signalr.html` page; nav links added across existing pages; sitemap.
- `release.yml`: add the new csproj to the pack list (lockstep versioning, project reference
  becomes a NuGet dependency at the same version).
- `Nabu.NET.sln`: new projects added; CI builds the solution and needs no change.

## Out of scope

- Client-to-server streaming parameters (`IAsyncEnumerable<T>` / `ChannelReader<T>` hub method
  *parameters*) — methods with them are skipped with a log line.
- netstandard2.0 target; MessagePack hub protocol (JSON only for the synthetic connection).
- Server-initiated MCP notifications.
