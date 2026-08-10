using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;
using Nabu.Mcp.AspNetCore.Protocol;
using Nabu.Mcp.AspNetCore.Schema;

namespace Nabu.Mcp.AspNetCore.Server
{
    /// <summary>Implements the MCP methods on top of the tool registry and invoker.</summary>
    public class McpRequestHandler
    {
        private readonly IMcpToolRegistry _registry;
        private readonly IMcpToolInvoker _invoker;
        private readonly NabuMcpOptions _options;
        private readonly IMcpToolAuthorizationEvaluator _authorization;
        private readonly ILogger _logger;

        public McpRequestHandler(
            IMcpToolRegistry registry,
            IMcpToolInvoker invoker,
            IOptions<NabuMcpOptions> options,
            ILogger<McpRequestHandler>? logger = null,
            IMcpToolAuthorizationEvaluator? authorization = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _authorization = authorization ?? new McpToolAuthorizationEvaluator(options);
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        /// <summary>
        /// Handles one JSON-RPC message. Returns <c>null</c> for notifications, which carry no response.
        /// </summary>
        public async Task<JsonNode?> HandleAsync(JsonRpcRequest request, HttpContext context, CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            try
            {
                switch (request.Method)
                {
                    case "initialize":
                        return JsonRpc.Result(request.Id, Initialize(request.Parameters));

                    case "ping":
                        return JsonRpc.Result(request.Id, new JsonObject());

                    case "tools/list":
                        return JsonRpc.Result(request.Id, await ListToolsAsync(context, cancellationToken).ConfigureAwait(false));

                    case "tools/call":
                        return JsonRpc.Result(request.Id, await CallToolAsync(request, context, cancellationToken).ConfigureAwait(false));

                    // Advertised as empty so clients that probe these methods do not surface an error.
                    case "resources/list":
                        return JsonRpc.Result(request.Id, new JsonObject { ["resources"] = new JsonArray() });

                    case "resources/templates/list":
                        return JsonRpc.Result(request.Id, new JsonObject { ["resourceTemplates"] = new JsonArray() });

                    case "prompts/list":
                        return JsonRpc.Result(request.Id, new JsonObject { ["prompts"] = new JsonArray() });

                    case "logging/setLevel":
                        return JsonRpc.Result(request.Id, new JsonObject());

                    default:
                        if (request.Method.StartsWith("notifications/", StringComparison.Ordinal))
                        {
                            return null;
                        }

                        return request.IsNotification
                            ? null
                            : JsonRpc.Error(request.Id, McpConstants.MethodNotFound, "Method '" + request.Method + "' is not supported.");
                }
            }
            catch (McpArgumentException ex)
            {
                return JsonRpc.Error(request.Id, McpConstants.InvalidParams, ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nabu MCP failed to handle method {Method}.", request.Method);
                return JsonRpc.Error(request.Id, McpConstants.InternalError, ex.Message);
            }
        }

        private JsonObject Initialize(JsonNode? parameters)
        {
            var requested = parameters?["protocolVersion"]?.GetValue<string>();
            var version = McpConstants.ProtocolVersion;

            if (!string.IsNullOrEmpty(requested))
            {
                foreach (var supported in McpConstants.SupportedProtocolVersions)
                {
                    if (string.Equals(supported, requested, StringComparison.Ordinal))
                    {
                        version = supported;
                        break;
                    }
                }
            }

            var result = new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject { ["listChanged"] = false },
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = _options.ServerName ?? "nabu-mcp",
                    ["version"] = _options.ServerVersion ?? "1.0.0",
                },
            };

            if (!string.IsNullOrEmpty(_options.Instructions))
            {
                result["instructions"] = _options.Instructions;
            }

            return result;
        }

        private async Task<JsonObject> ListToolsAsync(HttpContext context, CancellationToken cancellationToken)
        {
            var tools = new JsonArray();
            var hidden = 0;

            foreach (var tool in _registry.GetTools())
            {
                if (!await IsVisibleAsync(tool, context, cancellationToken).ConfigureAwait(false))
                {
                    hidden++;
                    continue;
                }

                var entry = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["inputSchema"] = JsonHelpers.Clone(tool.InputSchema),
                };

                if (!string.IsNullOrEmpty(tool.Annotations.Title))
                {
                    entry["title"] = tool.Annotations.Title;
                }

                if (!string.IsNullOrEmpty(tool.Description))
                {
                    entry["description"] = tool.Description;
                }

                entry["annotations"] = new JsonObject
                {
                    ["title"] = tool.Annotations.Title,
                    ["readOnlyHint"] = tool.Annotations.ReadOnly,
                    ["destructiveHint"] = tool.Annotations.Destructive,
                    ["idempotentHint"] = tool.Annotations.Idempotent,
                    ["openWorldHint"] = tool.Annotations.OpenWorld,
                };

                tools.Add(entry);
            }

            if (hidden > 0)
            {
                _logger.LogDebug(
                    "Nabu MCP hid {HiddenCount} tool(s) from this caller; {VisibleCount} advertised.",
                    hidden,
                    tools.Count);
            }

            return new JsonObject { ["tools"] = tools };
        }

        /// <summary>
        /// Whether a tool is advertised to the current caller. A failure to decide leaves the tool
        /// visible: the pipeline authorizes the call anyway, so an unfilterable tool costs a 403, while a
        /// wrongly hidden one costs the model a capability it actually has.
        /// </summary>
        private async Task<bool> IsVisibleAsync(McpToolDescriptor tool, HttpContext context, CancellationToken cancellationToken)
        {
            if (_options.ToolVisibility == McpToolVisibility.All)
            {
                return true;
            }

            try
            {
                return await _authorization.IsVisibleAsync(tool, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Nabu MCP could not decide whether to advertise the tool {Tool}; advertising it.", tool.Name);
                return true;
            }
        }

        /// <summary>
        /// Whether this payload has to pass <see cref="NabuMcpOptions.RequireAuthorization"/> before it is
        /// answered. Everything does unless <see cref="NabuMcpOptions.AnonymousAccess"/> opens a door, in
        /// which case discovery - and, at the widest setting, calls to tools that need no authorization -
        /// is answered for callers that hold no credentials yet.
        /// </summary>
        internal async Task<bool> RequiresEndpointAuthorizationAsync(
            IReadOnlyList<JsonRpcRequest> requests,
            HttpContext context,
            CancellationToken cancellationToken)
        {
            var access = _options.AnonymousAccess;
            if (access == McpAnonymousAccess.None)
            {
                return true;
            }

            foreach (var request in requests)
            {
                if (IsDiscoveryMethod(request.Method))
                {
                    continue;
                }

                if (access == McpAnonymousAccess.AnonymousTools &&
                    string.Equals(request.Method, "tools/call", StringComparison.Ordinal) &&
                    await IsAnonymousToolAsync(request, context, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Whether this call targets a tool the application itself would let an anonymous caller reach.
        /// Anything unclear counts as protected: this decides who gets in, so it errs towards the
        /// challenge rather than towards the door.
        /// </summary>
        private async Task<bool> IsAnonymousToolAsync(JsonRpcRequest request, HttpContext context, CancellationToken cancellationToken)
        {
            string? name;
            try
            {
                name = request.Parameters?["name"]?.GetValue<string>();
            }
            catch (InvalidOperationException)
            {
                // A non-string name; let the authorized path report it as an invalid argument.
                return false;
            }

            McpToolDescriptor? tool;
            if (string.IsNullOrEmpty(name) || !_registry.TryGetTool(name!, out tool))
            {
                return false;
            }

            try
            {
                return !await _authorization.RequiresAuthorizationAsync(tool!, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Nabu MCP could not decide whether {Tool} is reachable anonymously; requiring authorization.",
                    tool!.Name);
                return false;
            }
        }

        private static bool IsDiscoveryMethod(string method)
        {
            switch (method)
            {
                case "initialize":
                case "ping":
                case "tools/list":
                case "resources/list":
                case "resources/templates/list":
                case "prompts/list":
                case "logging/setLevel":
                    return true;

                default:
                    return method.StartsWith("notifications/", StringComparison.Ordinal);
            }
        }

        private async Task<JsonObject> CallToolAsync(JsonRpcRequest request, HttpContext context, CancellationToken cancellationToken)
        {
            var name = request.Parameters?["name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name))
            {
                throw new McpArgumentException("The 'name' parameter is required for tools/call.");
            }

            McpToolDescriptor? tool;
            if (!_registry.TryGetTool(name!, out tool))
            {
                throw new McpArgumentException("Unknown tool '" + name + "'.");
            }

            var arguments = request.Parameters?["arguments"] as JsonObject;

            McpToolInvocationResult result;
            try
            {
                result = await _invoker.InvokeAsync(tool!, arguments, context, cancellationToken).ConfigureAwait(false);
            }
            catch (McpArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Nabu MCP tool {Tool} threw while being invoked.", name);
                return ToolError("The tool failed to execute: " + ex.Message);
            }

            return BuildToolResult(tool!, result);
        }

        internal JsonObject BuildToolResult(McpToolDescriptor tool, McpToolInvocationResult result)
        {
            var isError = _options.TreatErrorStatusAsToolError && !result.IsSuccess;

            var text = result.Body;
            if (string.IsNullOrEmpty(text))
            {
                text = isError
                    ? "The request failed with HTTP status " + result.StatusCode.ToString(CultureInfo.InvariantCulture) + "."
                    : "The request completed with HTTP status " + result.StatusCode.ToString(CultureInfo.InvariantCulture) + " and an empty body.";
            }
            else if (isError)
            {
                text = "HTTP " + result.StatusCode.ToString(CultureInfo.InvariantCulture) + ": " + text;
            }

            if (result.Truncated)
            {
                text += Environment.NewLine + "[truncated: the response exceeded " +
                        _options.MaxResponseBytes.ToString(CultureInfo.InvariantCulture) + " bytes]";
            }

            var payload = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = isError,
            };

            if (_options.IncludeStructuredContent && !isError && !result.Truncated)
            {
                var structured = TryParseStructured(result);
                if (structured != null)
                {
                    payload["structuredContent"] = structured;
                }
            }

            return payload;
        }

        private static JsonNode? TryParseStructured(McpToolInvocationResult result)
        {
            if (string.IsNullOrEmpty(result.Body))
            {
                return null;
            }

            var contentType = result.ContentType;
            if (contentType != null && contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            try
            {
                var node = JsonNode.Parse(result.Body);

                // structuredContent must be an object; wrap arrays and scalars so clients stay happy.
                if (node is JsonObject)
                {
                    return node;
                }

                return node == null ? null : new JsonObject { ["result"] = node };
            }
            catch (JsonException)
            {
                return null;
            }
        }

        internal static JsonObject ToolError(string message)
        {
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
                ["isError"] = true,
            };
        }

        /// <summary>Parses a JSON-RPC payload into requests. Throws <see cref="JsonException"/> on bad JSON.</summary>
        internal static IReadOnlyList<JsonRpcRequest> ParseRequests(JsonNode? payload, out bool isBatch)
        {
            isBatch = payload is JsonArray;
            var requests = new List<JsonRpcRequest>();

            if (payload is JsonArray array)
            {
                foreach (var item in array)
                {
                    requests.Add(ParseSingle(item));
                }

                return requests;
            }

            requests.Add(ParseSingle(payload));
            return requests;
        }

        private static JsonRpcRequest ParseSingle(JsonNode? node)
        {
            var obj = node as JsonObject;
            if (obj == null)
            {
                throw new McpInvalidRequestException("A JSON-RPC message must be an object.");
            }

            var method = obj["method"]?.GetValue<string>();
            if (string.IsNullOrEmpty(method))
            {
                throw new McpInvalidRequestException("A JSON-RPC message must carry a 'method'.");
            }

            var id = obj["id"];
            if (id != null && id.GetValueKind() == JsonValueKind.Null)
            {
                id = null;
            }

            return new JsonRpcRequest(id, method!, obj["params"]);
        }
    }

    /// <summary>Raised for payloads that are valid JSON but not valid JSON-RPC.</summary>
    public sealed class McpInvalidRequestException : Exception
    {
        public McpInvalidRequestException(string message)
            : base(message)
        {
        }
    }
}
