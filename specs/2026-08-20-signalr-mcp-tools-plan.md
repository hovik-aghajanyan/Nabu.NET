# Nabu.Mcp.AspNetCore.SignalR Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish SignalR hub methods as MCP tools in the same `/mcp` catalogue as HTTP tools, invoked through a synthetic in-process SignalR connection over the app's real `HubConnectionHandler<THub>`.

**Architecture:** Two small non-breaking extension points in the core package (`IMcpToolSource` aggregation in `McpToolRegistry`, per-descriptor `InvokerType` dispatch in both protocol layers), plus a new `net8.0;net10.0` package containing a hub tool source (discovery from `EndpointDataSource`/`HubMetadata`) and a hub tool invoker (TestClient-style pipe-pair connection through the real dispatcher). Hub-class-level `[Authorize]` is the connection gate and is evaluated by the invoker; method-level `[Authorize]` is enforced by the real dispatcher.

**Tech Stack:** ASP.NET Core SignalR (shared framework), System.IO.Pipelines, JsonHubProtocol, existing Nabu core (JsonSchemaGenerator, XML docs, ToolNameFactory, McpToolAuthorization, visibility evaluator).

**Spec:** `specs/2026-08-20-signalr-mcp-tools-design.md`

## Global Constraints

- New package TFMs: `net10.0;net8.0` (no netstandard2.0), mirroring `Nabu.Mcp.ModelContextProtocol`.
- Core changes must be non-breaking: existing constructors/behavior untouched; netstandard2.0 target of core must keep compiling (guard modern-only code with `#if !NETSTANDARD2_0` where needed — `IMcpToolSource` itself is TFM-neutral).
- All work committed locally on `main`; **never push**.
- Naming: `ChatHub.SendMessage` → default tool name `chat_send_message` via existing `ToolNameFactory` (`McpToolNamingContext(controllerName: "Chat", actionName: "SendMessage", httpMethod: "HUB", routeTemplate: "<hub-pattern>/<Method>")`).
- SignalR semantics for auth: hub-class `[Authorize]` gates the (synthetic) connection for **all** methods — a method-level `[AllowAnonymous]` does not bypass it (matches real SignalR, where you cannot connect at all). Method-level `[Authorize]` is enforced by `DefaultHubDispatcher`.
- Methods with client-to-server streaming parameters (`ChannelReader<T>`/`IAsyncEnumerable<T>` parameters) are skipped with a log warning.
- Result JSON shape: plain return value when nothing else captured; else `{ "result": ..., "callerMessages": [{"method": ..., "arguments": [...]}], "streamItems": [...], "truncated": bool }`.

---

### Task 1: Core — `IMcpToolSource` + registry aggregation

**Files:**
- Create: `src/Nabu.Mcp.AspNetCore/Discovery/IMcpToolSource.cs`
- Modify: `src/Nabu.Mcp.AspNetCore/Discovery/McpToolRegistry.cs` (ctor overload + `Build` merge)
- Modify: `src/Nabu.Mcp.AspNetCore/DependencyInjection/NabuMcpServiceCollectionExtensions.cs` (pass `sp.GetServices<IMcpToolSource>()`)
- Test: `tests/Nabu.Mcp.AspNetCore.Tests/Unit/ToolSourceTests.cs`

**Interfaces:**
- Produces: `public interface IMcpToolSource { IReadOnlyList<McpToolDescriptor> GetTools(); }` (namespace `Nabu.Mcp.AspNetCore.Discovery`).
- Registry ctor gains optional `IEnumerable<IMcpToolSource>? toolSources = null` (new overload; existing signatures preserved). Source tools are appended after endpoint tools, deduplicated against `used` names with the existing suffix+warn behavior, run through `ToolFilter`, included in the sort.

- [ ] Failing test: registry with a fake `IMcpToolSource` returning one descriptor surfaces it from `GetTools()`/`TryGetTool`; name collision with an existing tool gets `_2` suffix; `ToolFilter` can drop it.
- [ ] Implement; run `dotnet test`; commit.

### Task 2: Core — source-agnostic descriptor + per-descriptor invoker dispatch

**Files:**
- Modify: `src/Nabu.Mcp.AspNetCore/Discovery/McpToolDescriptor.cs` — add ctor `(string name, IReadOnlyList<McpToolParameterDescriptor> parameters, JsonObject inputSchema, McpToolAnnotations annotations)` setting `HttpMethod = ""`, `RouteTemplate = ""`; add `public Type? InvokerType { get; set; }`; `ToString()` prints just the name when `HttpMethod` is empty.
- Modify: `src/Nabu.Mcp.AspNetCore/Server/McpRequestHandler.cs` — in `CallToolAsync`: `var invoker = tool.InvokerType != null ? (IMcpToolInvoker)context.RequestServices.GetRequiredService(tool.InvokerType) : _invoker;`. In `BuildToolResult`, use non-HTTP wording when `tool.HttpMethod.Length == 0` ("The tool failed." / "The tool completed with an empty result." / error prefix without "HTTP").
- Modify: `src/Nabu.Mcp.ModelContextProtocol/NabuOfficialMcpBridge.cs` — same invoker resolution from `httpContext.RequestServices`.
- Test: `tests/Nabu.Mcp.AspNetCore.Tests/Unit/ToolSourceTests.cs` (dispatch test with a fake invoker type registered in DI).

- [ ] Failing test → implement → tests pass → commit.

### Task 3: New project `Nabu.Mcp.AspNetCore.SignalR` — csproj, options, DI

**Files:**
- Create: `src/Nabu.Mcp.AspNetCore.SignalR/Nabu.Mcp.AspNetCore.SignalR.csproj` (mirror `Nabu.Mcp.ModelContextProtocol.csproj`: TFMs `net10.0;net8.0`, `FrameworkReference Microsoft.AspNetCore.App`, `ProjectReference` core, package metadata, README/LICENSE pack, `InternalsVisibleTo Nabu.Mcp.AspNetCore.SignalR.Tests`)
- Create: `src/Nabu.Mcp.AspNetCore.SignalR/NabuMcpSignalROptions.cs` — `ExposeAllHubMethods` (false), `MaxStreamItems` (1000), `MaxCallerMessages` (100), `InvocationTimeout` (TimeSpan, default 30s)
- Create: `src/Nabu.Mcp.AspNetCore.SignalR/DependencyInjection/NabuMcpSignalRServiceCollectionExtensions.cs` — `public static IServiceCollection AddNabuMcpSignalR(this IServiceCollection services, Action<NabuMcpSignalROptions>? configure = null)`: registers `SignalRHubToolSource` as `IMcpToolSource` (TryAddEnumerable), `SignalRHubToolInvoker` as itself (singleton).
- Modify: `Nabu.NET.sln` (add project)

- [ ] Scaffold, `dotnet build` green, commit.

### Task 4: Discovery — `SignalRHubToolSource`

**Files:**
- Create: `src/Nabu.Mcp.AspNetCore.SignalR/Discovery/SignalRHubToolSource.cs`
- Create: `tests/Nabu.Mcp.AspNetCore.SignalR.Tests/` project (xunit, mirrors core test csproj; references SignalR package project) + `Unit/HubToolDiscoveryTests.cs`

**Interfaces:**
- Consumes: `IMcpToolSource`, `McpToolDescriptor` source-agnostic ctor + `InvokerType`, `JsonSchemaGenerator.Generate(Type, attrs, nullable)`, `IXmlDocumentationProvider`, `NabuMcpOptions.ToolNameFactory`, `McpToolAuthorization`.
- Produces: descriptors whose `Parameters` use `McpParameterSource.Body`-style positional binding recorded via `BindingName` = parameter name and order preserved; sets `InvokerType = typeof(SignalRHubToolInvoker)`; attaches internal `SignalRHubToolMetadata` (hub `Type`, `MethodInfo`, streaming flag, class-level `IAuthorizeData` list) via an internal property or a `ConditionalWeakTable`/dictionary keyed by descriptor (choose: `internal sealed class SignalRHubToolMetadata`; store on descriptor via new internal-friendly mechanism — simplest is an `internal static ConditionalWeakTable<McpToolDescriptor, SignalRHubToolMetadata>` in the SignalR package).

Behavior:
- Enumerate `EndpointDataSource.Endpoints`, find endpoints with `HubMetadata` (via `endpoint.Metadata.GetMetadata<HubMetadata>()` — public type in `Microsoft.AspNetCore.SignalR`), take hub `Type` + route pattern; dedupe hub types.
- Hub methods: public instance methods declared on the hub type (walk up to but excluding `Hub`/`Hub<T>`/`object`), non-special-name, not `OnConnectedAsync`/`OnDisconnectedAsync`, honoring `[McpIgnore]` (class/method), `[McpTool]` variants (method-level replaces class-level, same rules as MVC path), `ExposeAllHubMethods` for unannotated methods.
- Return type mapping: `Task<T>`/`ValueTask<T>`/`T` → non-streaming; `IAsyncEnumerable<T>`/`ChannelReader<T>` (incl. wrapped in Task) → streaming.
- Parameters: skip `CancellationToken`; skip client-streaming methods entirely (warn). Schema per parameter from `JsonSchemaGenerator`; required = non-nullable && no default; `[McpParameter]` overrides honored; XML `<param>` descriptions.
- Annotations: default `ReadOnly=false, Destructive=false, Idempotent=false, OpenWorld=false`; attribute overrides win.
- Authorization: `[AllowAnonymous]`/`[Authorize]` from method + class → `McpToolAuthorization(allowsAnonymous: method-or-class AllowAnonymous && class has no [Authorize] ... )` — per Global Constraints, class `[Authorize]` wins over method `[AllowAnonymous]`; compute `allowsAnonymous = classAllowsAnonymous || (methodAllowsAnonymous && !classHasAuthorize)`; `AuthorizeData` = class + method authorize data.
- Naming/variants: reuse attribute variant semantics (Include/Exclude/Constant/Required/OptionalParameters) — constants for hub tools pin positional arguments by name (`McpToolConstantDescriptor` with `Source = Body`).

- [ ] Failing unit tests (discovery of annotated hub, naming, ignore, streaming param skip, auth metadata) → implement → pass → commit.

### Task 5: Invocation — synthetic connection + `SignalRHubToolInvoker`

**Files:**
- Create: `src/Nabu.Mcp.AspNetCore.SignalR/Execution/SyntheticHubConnection.cs` — internal: pipe pair (`System.IO.Pipelines.Pipe` × 2 exposed as two `IDuplexPipe`s), a minimal `ConnectionContext` subclass carrying `Features` (`IConnectionUserFeature` with the caller's `ClaimsPrincipal`, `IConnectionItemsFeature`, `IHttpContextFeature` seeded with the MCP `HttpContext`), `ConnectionId = Guid`, `RequestServices` from a fresh scope.
- Create: `src/Nabu.Mcp.AspNetCore.SignalR/Execution/SignalRHubToolInvoker.cs` — implements `IMcpToolInvoker`.

**Interfaces:**
- Consumes: `SignalRHubToolMetadata` (hub type, MethodInfo, streaming flag, class auth data), `NabuMcpSignalROptions`, `IAuthorizationService`/`IAuthorizationPolicyProvider` (class gate), `IServiceScopeFactory`.
- Produces: `McpToolInvocationResult` with `StatusCode` 200 (success) / 403 (class gate) / 500 (hub error), `ContentType = "application/json"`, `Body` = result JSON per Global Constraints shape.

Flow per call:
1. Class gate: if class `[Authorize]` data present and not `AllowAnonymous`, combine via `AuthorizationPolicy.CombineAsync(policyProvider, authorizeData)` and evaluate against `originalContext.User`; failure → 403 result (`isError` downstream).
2. Bind arguments: positional `object?[]` of `JsonElement`s in method parameter order; missing optional → parameter default; missing required → `McpArgumentException`; constants win over caller args.
3. Open synthetic connection: resolve `HubConnectionHandler<THub>` via non-generic `(ConnectionHandler)scope.ServiceProvider.GetRequiredService(typeof(HubConnectionHandler<>).MakeGenericType(hubType))`; run `handler.OnConnectedAsync(connection)` on a background task.
4. Handshake: write `HandshakeRequestMessage("json", 1)` via `HandshakeProtocol.WriteRequestMessage`; read/parse response with `HandshakeProtocol.TryParseResponseMessage`.
5. Invoke: `JsonHubProtocol.WriteMessage(new InvocationMessage("1", methodName, args), output)` (or `StreamInvocationMessage` for streaming tools).
6. Read loop with `options.InvocationTimeout` + caller cancellation: `TryParseMessage` → collect `StreamItemMessage` (cap `MaxStreamItems`, then send `CancelInvocationMessage` and mark truncated), caller-directed `InvocationMessage` (cap `MaxCallerMessages`, mark truncated), stop at `CompletionMessage` (error → 500 result with error text; result → serialize).
7. Close: complete the application output, await the handler task (bounded), dispose scope.

- [ ] Failing unit tests for argument binding (missing required, defaults, constants) → implement binder → pass → commit.
- [ ] Integration-shaped unit test with a real `ServiceCollection` (`AddSignalR().AddLogging()` + test hub): invoke echo method end-to-end through `HubConnectionHandler` → pass → commit.

### Task 6: Integration tests (auth parity is mandatory)

**Files:**
- Create: `tests/Nabu.Mcp.AspNetCore.SignalR.Tests/Integration/SignalRTestFixture.cs` — in-memory app (`WebApplicationFactory`-style or `new WebHostBuilder().UseTestServer()`) with JWT test auth (mirror `McpTestFixture` patterns from the core test project), `AddSignalR`, `MapHub<TestChatHub>("/hubs/chat")`, `AddNabuMcp` + `AddNabuMcpSignalR` + one HTTP `[McpTool]` controller for merge assertions.
- Create: `Integration/HubToolDiscoveryTests.cs`, `Integration/HubToolInvocationTests.cs`, `Integration/HubAuthorizationTests.cs`, `Integration/HubStreamingTests.cs`, `Integration/CallerMessageTests.cs`, `Integration/HubToolVisibilityTests.cs`

Must assert:
- `tools/list` merges `chat_*` tools with HTTP tools on the built-in layer.
- Echo invocation returns the value; structuredContent present.
- Method-level `[Authorize(Policy="AdminOnly")]`: alice → `isError: true` with auth failure, root → success (same-caller parity claim).
- Hub-class-level `[Authorize]`: anonymous caller gets `isError` even on a method with no attributes; authenticated caller succeeds.
- `ToolVisibility = Authorized`: anonymous `tools/list` hides protected hub tools, shows anonymous ones.
- Streaming method collects items; cap truncates and flags.
- `Clients.Caller.SendAsync` captured into `callerMessages`; `Clients.All` reaches a real connected test client (use `HubConnection` over the TestServer, or assert via `IHubContext` capture — simplest: a second synthetic client connected for the duration).
- One user's data never leaks to another (per-user method using `Context.UserIdentifier`).

- [ ] Write tests → red → fix implementation → green → commit.

### Task 7: Sample `samples/Nabu.Sample.ChatHub`

**Files:**
- Create: `samples/Nabu.Sample.ChatHub/Nabu.Sample.ChatHub.csproj` (net10.0, like other samples; `GenerateDocumentationFile`), `Program.cs`, `Hubs/ChatHub.cs`, `Services/TokenService.cs` + `Models/AuthModels.cs` (copy the OfficialSdk sample's JWT setup, accounts alice/root, password `password`), `Services/ChatHistory.cs` (in-memory), `Controllers/AuthController.cs` (login endpoint), `wwwroot/index.html` (vanilla JS SignalR client via the `signalr.js` bundle from unpkg CDN, joins the hub, renders broadcasts)
- Modify: `Nabu.NET.sln`

`ChatHub` (`[Authorize]` on class is wrong for demo of anonymous methods — instead): class unannotated; methods:
- `GetRecentMessages(int count)` — `[McpTool]`, anonymous, returns history.
- `SendMessage(string text)` — `[Authorize]` + `[McpTool]`, broadcasts via `Clients.All`, returns the created message.
- `DeleteMessage(Guid id)` — `[Authorize(Policy="AdminOnly")]` + `[McpTool]`, admin only.
- `WhoAmI()` — `[Authorize]` + `[McpTool]`, replies via `Clients.Caller.SendAsync("whoami", ...)` and returns nothing → demonstrates caller capture.
- `StreamRecentMessages(int count)` — `[McpTool]`, returns `IAsyncEnumerable<ChatMessage>` → demonstrates streaming collection.
- Plus a second hub `PresenceHub` with `[Authorize]` on the **class** to demonstrate the class gate.

- [ ] Build + run sample, exercise `/mcp` with curl (tools/list anonymous vs token; send_message with alice; verify browser page receives broadcast), commit.

### Task 8: Docker/Inspector wiring

**Files:**
- Modify: `docker-compose.yml`, `docker/` init scripts + Inspector config generation (read them first) — add chat sample on port `5082`, connections `chat-anonymous`, `chat-alice-user`, `chat-root-admin`.

- [ ] Update, validate compose config parses (`docker compose config -q`), commit. (Full docker run optional if Docker unavailable.)

### Task 9: Docs — README + GitHub Pages + release workflow

**Files:**
- Modify: `README.md` — new "SignalR hubs" section (registration, attribute usage, invocation model + per-call OnConnected caveat, class-gate semantics, streaming/caller capture, options table), repo layout, packages badge/mention, test counts.
- Create: `docs/signalr.html` — follow the structure/nav/style of `docs/minimal-apis.html` (read it first; reuse `assets/style.css`, nav, OG tags).
- Modify: nav links in `docs/index.html`, `docs/getting-started.html`, `docs/minimal-apis.html`, `docs/attributes-and-tools.html`, `docs/configuration.html`, `docs/security.html`; `docs/sitemap.xml`.
- Modify: `.github/workflows/release.yml` — add `src/Nabu.Mcp.AspNetCore.SignalR/Nabu.Mcp.AspNetCore.SignalR.csproj` to the pack loop; comment already covers lockstep versioning.
- Verify: `.github/workflows/ci.yml` needs no change (builds the solution).

- [ ] Write docs, validate HTML links locally, commit.

### Task 10: Final verification + knowledge base

- [ ] `dotnet build Nabu.NET.sln` (all TFMs) and `dotnet test Nabu.NET.sln` — full green; fix anything.
- [ ] Mirror the spec + a project note into the Obsidian vault (`projects/Nabu/Nabu.NET/`), create `Nabu-memory.md`, link from vault `MEMORY.md` (per global CLAUDE.md).
- [ ] Final commit; **do not push** — summarize for owner review.

## Self-review

- Spec coverage: decisions 1–4 → Tasks 4/5 (invocation, streaming, caller capture), 1/2 (merged catalogue); verified constraints → Task 5 flow + Task 6 parity tests; sample → 7/8; docs/CI/release → 9; out-of-scope respected (client streaming skipped in Task 4).
- Types consistent: `IMcpToolSource.GetTools()`, `McpToolDescriptor.InvokerType`, `SignalRHubToolInvoker : IMcpToolInvoker`, `AddNabuMcpSignalR` used consistently across tasks.
