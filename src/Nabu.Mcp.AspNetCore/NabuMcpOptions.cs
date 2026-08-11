using System;
using System.Collections.Generic;
using Nabu.Mcp.AspNetCore.Discovery;

namespace Nabu.Mcp.AspNetCore
{
    /// <summary>Which of the discovered tools <c>tools/list</c> advertises to a given caller.</summary>
    public enum McpToolVisibility
    {
        /// <summary>
        /// Every discovered tool is advertised, whoever is asking. Calls are still authorized by the
        /// application pipeline, so a caller can see a tool it is not allowed to invoke. This is the
        /// default.
        /// </summary>
        All = 0,

        /// <summary>
        /// Tools whose actions require authorization are advertised only to callers that are
        /// authenticated. Which policies those callers satisfy is not examined - use
        /// <see cref="Authorized"/> for that.
        /// </summary>
        Authenticated = 1,

        /// <summary>
        /// Tools whose actions require authorization are advertised only to callers that satisfy the
        /// action's authorization policies, evaluated with the application's own policy provider and
        /// authorization service.
        /// </summary>
        Authorized = 2,
    }

    /// <summary>
    /// How much of the MCP endpoint an unauthorized caller may reach when
    /// <see cref="NabuMcpOptions.RequireAuthorization"/> is on.
    /// </summary>
    public enum McpAnonymousAccess
    {
        /// <summary>
        /// Nothing: every request to the endpoint is subject to <see cref="NabuMcpOptions.RequireAuthorization"/>.
        /// This is the default.
        /// </summary>
        None = 0,

        /// <summary>
        /// <c>initialize</c>, <c>ping</c> and the listing methods are answered without authorization, so a
        /// client can be added before credentials exist. <c>tools/call</c> still requires them.
        /// </summary>
        Discovery = 1,

        /// <summary>
        /// As <see cref="Discovery"/>, and additionally <c>tools/call</c> for tools whose actions do not
        /// require authorization. Everything else still challenges, and the action's own authorization
        /// runs as usual.
        /// </summary>
        AnonymousTools = 2,
    }

    /// <summary>Configuration for the MCP server exposed on top of an existing Web API.</summary>
    public class NabuMcpOptions
    {
        /// <summary>Server name reported during <c>initialize</c>. Defaults to the entry assembly name.</summary>
        public string? ServerName { get; set; }

        /// <summary>Server version reported during <c>initialize</c>. Defaults to the entry assembly version.</summary>
        public string? ServerVersion { get; set; }

        /// <summary>Free-form guidance handed to the model alongside the tool list.</summary>
        public string? Instructions { get; set; }

        /// <summary>
        /// Freshness hint, in milliseconds, stamped as <c>ttlMs</c> on the cacheable results
        /// (<c>server/discover</c> and the listing methods) served to 2026-07-28 clients. The tool
        /// registry is fixed at startup, but what a caller is shown can depend on its credentials,
        /// so keep this short enough that a client re-lists soon after authenticating.
        /// </summary>
        public int CacheTtlMilliseconds { get; set; } = 60_000;

        /// <summary>Path the MCP endpoint is served from. Defaults to <c>/mcp</c>.</summary>
        public string Path { get; set; } = "/mcp";

        /// <summary>
        /// When <c>true</c>, every discovered API action becomes a tool unless it carries
        /// <see cref="McpIgnoreAttribute"/>. Defaults to <c>false</c>: only actions or controllers
        /// annotated with <see cref="McpToolAttribute"/> are exposed.
        /// </summary>
        public bool ExposeAllActions { get; set; }

        /// <summary>
        /// Requires an authenticated principal on the MCP endpoint itself. This is independent of the
        /// authorization applied to each underlying action, which always runs.
        /// </summary>
        public bool RequireAuthorization { get; set; }

        /// <summary>
        /// Authorization policy evaluated on the MCP endpoint when <see cref="RequireAuthorization"/>
        /// is set. <c>null</c> means "use the default policy".
        /// </summary>
        public string? AuthorizationPolicy { get; set; }

        /// <summary>
        /// Authentication schemes to challenge with on the MCP endpoint. Empty means the default scheme.
        /// </summary>
        public IList<string> AuthenticationSchemes { get; } = new List<string>();

        /// <summary>
        /// Tailors the advertised tool list to the caller. With
        /// <see cref="McpToolVisibility.Authenticated"/> or <see cref="McpToolVisibility.Authorized"/> an
        /// unauthorized caller is shown only the tools whose actions it could actually invoke, and the
        /// rest appear once it has authenticated and listed the tools again.
        /// </summary>
        /// <remarks>
        /// This decides what is <em>advertised</em>, never what is allowed: every tool call still travels
        /// through the application pipeline and is authorized there. A tool hidden from a caller that
        /// calls it anyway is refused by the action itself, exactly as an HTTP request would be.
        /// </remarks>
        public McpToolVisibility ToolVisibility { get; set; } = McpToolVisibility.All;

        /// <summary>
        /// Lets an unauthorized caller reach part of the MCP endpoint even though
        /// <see cref="RequireAuthorization"/> is on, so a client can be added, initialized and given a
        /// tool list before it holds any credentials. Ignored when <see cref="RequireAuthorization"/> is
        /// off, because the endpoint is then anonymous already.
        /// </summary>
        /// <remarks>
        /// Pair this with <see cref="ToolVisibility"/>, otherwise the anonymous caller is advertised
        /// tools it cannot call.
        /// </remarks>
        public McpAnonymousAccess AnonymousAccess { get; set; } = McpAnonymousAccess.None;

        /// <summary>
        /// Request headers copied from the MCP request onto the synthetic action request. Credentials
        /// live here, which is what keeps the caller's identity intact. Matching is case insensitive.
        /// </summary>
        public ISet<string> ForwardedHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Cookie",
            "Accept-Language",
            "User-Agent",
            "X-Forwarded-For",
            "X-Forwarded-Proto",
            "X-Forwarded-Host",
            "X-Request-Id",
            "X-Correlation-Id",
            "traceparent",
            "tracestate",
            "baggage",
        };

        /// <summary>
        /// Additional header prefixes forwarded to the action (for example <c>X-Tenant-</c>).
        /// </summary>
        public IList<string> ForwardedHeaderPrefixes { get; } = new List<string>();

        /// <summary>
        /// Exposes action parameters bound from headers as tool inputs. Off by default because header
        /// parameters usually carry infrastructure concerns rather than model-supplied data.
        /// </summary>
        public bool ExposeHeaderParameters { get; set; }

        /// <summary>
        /// Headers a model-supplied argument may never set on the synthetic request, even when
        /// <see cref="ExposeHeaderParameters"/> is on. Credentials and proxy metadata forwarded from
        /// the MCP caller keep their original values; a tool argument targeting one of these headers is
        /// dropped with a warning. Constants pinned by the developer through
        /// <see cref="McpToolAttribute.ConstantParameters"/> are not affected - they are developer
        /// input, not model input. Remove a name to explicitly allow the model to supply it.
        /// Matching is case insensitive.
        /// </summary>
        public ISet<string> ProtectedHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Cookie",
            "Host",
            "X-Forwarded-For",
            "X-Forwarded-Proto",
            "X-Forwarded-Host",
        };

        /// <summary>
        /// Seeds the synthetic request with the <see cref="System.Security.Claims.ClaimsPrincipal"/> that
        /// was authenticated for the MCP request. Authentication middleware still runs and overwrites it
        /// when the forwarded credentials authenticate successfully; this only makes identity survive
        /// schemes whose credentials cannot be replayed from headers alone. Leave enabled unless the MCP
        /// endpoint is reachable anonymously and the underlying actions must re-authenticate from scratch.
        /// </summary>
        public bool PropagateUser { get; set; } = true;

        /// <summary>
        /// Largest action response body, in bytes, turned into tool content. This bounds memory as well
        /// as output: the response is buffered into a capped stream that discards everything past the
        /// limit as it is written, so an action that produces a huge response never occupies more than
        /// this much response memory, and the result is flagged as truncated. Defaults to 1 MiB.
        /// </summary>
        public int MaxResponseBytes { get; set; } = 1024 * 1024;

        /// <summary>
        /// Emits the JSON response body as MCP <c>structuredContent</c> in addition to text content.
        /// </summary>
        public bool IncludeStructuredContent { get; set; } = true;

        /// <summary>
        /// Treats HTTP status codes &gt;= 400 as tool errors (<c>isError: true</c>) rather than plain
        /// results. Enabled by default so models can react to failures.
        /// </summary>
        public bool TreatErrorStatusAsToolError { get; set; } = true;

        /// <summary>
        /// Maximum nesting depth used when generating JSON schemas for complex request models.
        /// </summary>
        public int MaxSchemaDepth { get; set; } = 8;

        /// <summary>
        /// Whether the application's input formatters accept enum <em>names</em> in JSON request bodies.
        /// <c>null</c> (the default) detects it from the registered JSON options. Tool schemas always
        /// describe enums by name; when the application expects numbers, Nabu converts names to numbers
        /// while building the request body.
        /// </summary>
        public bool? StringEnumsInRequestBody { get; set; }

        /// <summary>
        /// Reads <c>&lt;summary&gt;</c> / <c>&lt;param&gt;</c> XML documentation for actions and models
        /// when no explicit description was supplied. Requires <c>GenerateDocumentationFile</c>.
        /// </summary>
        public bool UseXmlDocumentation { get; set; } = true;

        /// <summary>
        /// Flattens the properties of a single <c>[FromBody]</c> complex parameter into the top level of
        /// the tool input schema, so models fill one flat object instead of a nested wrapper.
        /// </summary>
        public bool FlattenBodyParameter { get; set; } = true;

        /// <summary>
        /// Builds the tool name for an action. Defaults to <c>controller_action</c> in snake_case.
        /// </summary>
        public Func<McpToolNamingContext, string> ToolNameFactory { get; set; } = DefaultToolName;

        /// <summary>Extra filter applied after attribute based discovery. Return false to drop a tool.</summary>
        public Func<McpToolDescriptor, bool>? ToolFilter { get; set; }

        internal static string DefaultToolName(McpToolNamingContext context)
        {
            var controller = ToSnakeCase(context.ControllerName);
            var action = ToSnakeCase(context.ActionName);
            return controller.Length == 0 ? action : controller + "_" + action;
        }

        internal static string ToSnakeCase(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(value.Length + 8);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsUpper(c))
                {
                    var previousIsLower = i > 0 && (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1]));
                    var nextIsLower = i + 1 < value.Length && char.IsLower(value[i + 1]);
                    if (i > 0 && (previousIsLower || nextIsLower) && builder.Length > 0 && builder[builder.Length - 1] != '_')
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(c));
                }
                else if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
                else if (builder.Length > 0 && builder[builder.Length - 1] != '_')
                {
                    builder.Append('_');
                }
            }

            return builder.ToString().Trim('_');
        }
    }

    /// <summary>Information handed to <see cref="NabuMcpOptions.ToolNameFactory"/>.</summary>
    public sealed class McpToolNamingContext
    {
        public McpToolNamingContext(string controllerName, string actionName, string httpMethod, string routeTemplate)
        {
            ControllerName = controllerName;
            ActionName = actionName;
            HttpMethod = httpMethod;
            RouteTemplate = routeTemplate;
        }

        /// <summary>Controller name without the "Controller" suffix.</summary>
        public string ControllerName { get; }

        public string ActionName { get; }

        public string HttpMethod { get; }

        public string RouteTemplate { get; }
    }
}
