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
        private readonly ILogger _logger;

        public McpRequestHandler(
            IMcpToolRegistry registry,
            IMcpToolInvoker invoker,
            IOptions<NabuMcpOptions> options,
            ILogger<McpRequestHandler>? logger = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
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
                        return JsonRpc.Result(request.Id, ListTools());

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

        private JsonObject ListTools()
        {
            var tools = new JsonArray();

            foreach (var tool in _registry.GetTools())
            {
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

            return new JsonObject { ["tools"] = tools };
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
