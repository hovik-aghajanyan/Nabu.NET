using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;
using NabuRequestHandler = Nabu.Mcp.AspNetCore.Server.McpRequestHandler;

namespace Nabu.Mcp.ModelContextProtocol
{
    /// <summary>
    /// Serves the official SDK's tool handlers from Nabu's registry and pipeline replay. The SDK
    /// owns the wire protocol; this type owns the semantics - which tools this caller is shown,
    /// and how a call becomes a replayed HTTP request.
    /// </summary>
    internal sealed class NabuOfficialMcpBridge
    {
        private readonly NabuRequestHandler _handler;
        private readonly IMcpToolRegistry _registry;
        private readonly IMcpToolInvoker _invoker;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly NabuMcpOptions _options;

        public NabuOfficialMcpBridge(
            NabuRequestHandler handler,
            IMcpToolRegistry registry,
            IMcpToolInvoker invoker,
            IHttpContextAccessor httpContextAccessor,
            IOptions<NabuMcpOptions> options)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        }

        public async ValueTask<ListToolsResult> ListToolsAsync(CancellationToken cancellationToken)
        {
            var httpContext = RequireHttpContext();

            // Reuses the built-in listing so authorization-aware visibility behaves identically
            // on both protocol layers.
            var payload = await _handler.ListToolsAsync(httpContext, cancellationToken).ConfigureAwait(false);

            var result = Deserialize<ListToolsResult>(payload);
            result.TimeToLive = TimeSpan.FromMilliseconds(_options.CacheTtlMilliseconds);
            result.CacheScope = CacheScope.Private;
            return result;
        }

        public async ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams parameters, CancellationToken cancellationToken)
        {
            if (parameters == null || string.IsNullOrEmpty(parameters.Name))
            {
                throw new McpException("The 'name' parameter is required for tools/call.");
            }

            var httpContext = RequireHttpContext();

            McpToolDescriptor? tool;
            if (!_registry.TryGetTool(parameters.Name, out tool))
            {
                throw new McpException("Unknown tool '" + parameters.Name + "'.");
            }

            JsonObject? arguments = null;
            if (parameters.Arguments != null)
            {
                arguments = new JsonObject();
                foreach (var argument in parameters.Arguments)
                {
                    arguments[argument.Key] = JsonNode.Parse(argument.Value.GetRawText());
                }
            }

            // Tools contributed by a non-HTTP source name their own invoker; everything else is
            // replayed through the HTTP pipeline, exactly as on the built-in protocol layer.
            var invoker = _invoker;
            if (tool!.InvokerType != null)
            {
                invoker = httpContext.RequestServices.GetService(tool.InvokerType) as IMcpToolInvoker
                    ?? throw new InvalidOperationException(
                        "Tool '" + tool.Name + "' names " + tool.InvokerType + " as its invoker, but that service is not registered.");
            }

            McpToolInvocationResult invocation;
            try
            {
                invocation = await invoker.InvokeAsync(tool!, arguments, httpContext, cancellationToken).ConfigureAwait(false);
            }
            catch (McpArgumentException ex)
            {
                throw new McpException(ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Failures inside the tool surface as isError content so the model can react,
                // exactly as on the built-in protocol layer.
                return Deserialize<CallToolResult>(NabuRequestHandler.ToolError("The tool failed to execute: " + ex.Message));
            }

            return Deserialize<CallToolResult>(_handler.BuildToolResult(tool!, invocation));
        }

        private HttpContext RequireHttpContext()
        {
            return _httpContextAccessor.HttpContext ?? throw new InvalidOperationException(
                "Nabu's tools replay the application's own HTTP pipeline and need the current HttpContext, " +
                "so the official-protocol bridge can only serve the HTTP transport.");
        }

        private static T Deserialize<T>(JsonObject payload)
        {
            return JsonSerializer.Deserialize<T>(payload, McpJsonUtilities.DefaultOptions)!;
        }
    }
}
