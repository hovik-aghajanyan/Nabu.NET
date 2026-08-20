using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;
using Nabu.Mcp.AspNetCore.Schema;
using Nabu.Mcp.AspNetCore.SignalR.Discovery;

namespace Nabu.Mcp.AspNetCore.SignalR.Execution
{
    /// <summary>
    /// Runs a hub-method tool the way the core package runs an HTTP tool - through the real thing.
    /// Each call opens a synthetic in-process connection and drives the application's own
    /// <c>HubConnectionHandler&lt;THub&gt;</c> over the JSON hub protocol, so hub filters,
    /// method-level authorization, <c>OnConnectedAsync</c> and streaming all behave exactly as they
    /// do for a live client. The one thing the dispatcher never enforces - the hub-class-level
    /// <c>[Authorize]</c>, which on a live connection is enforced by the HTTP negotiate - is
    /// evaluated here before the connection is opened.
    /// </summary>
    public sealed class SignalRHubToolInvoker : IMcpToolInvoker
    {
        private const byte RecordSeparator = 0x1e;
        private const string InvocationId = "1";

        private readonly NabuMcpSignalROptions _options;
        private readonly ILogger _logger;
        private McpJsonCompatibility? _compatibility;

        public SignalRHubToolInvoker(IOptions<NabuMcpSignalROptions> options, ILogger<SignalRHubToolInvoker>? logger = null)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        public async Task<McpToolInvocationResult> InvokeAsync(
            McpToolDescriptor tool,
            JsonObject? arguments,
            HttpContext originalContext,
            CancellationToken cancellationToken)
        {
            if (!SignalRHubToolSource.TryGetMetadata(tool, out var metadata) || metadata == null)
            {
                throw new InvalidOperationException(
                    "Tool '" + tool.Name + "' carries no SignalR invocation metadata. It was not discovered by SignalRHubToolSource.");
            }

            var gate = await EvaluateConnectionGateAsync(metadata, originalContext).ConfigureAwait(false);
            if (gate != null)
            {
                return gate;
            }

            var args = SignalRHubArgumentBinder.Bind(tool, metadata, arguments, DetectCompatibility(originalContext.RequestServices));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.InvocationTimeout);

            var connection = new SyntheticHubConnectionContext(originalContext.User, originalContext);
            var handler = (ConnectionHandler)originalContext.RequestServices.GetRequiredService(
                typeof(HubConnectionHandler<>).MakeGenericType(metadata.HubType));

            var serverTask = handler.OnConnectedAsync(connection);
            try
            {
                return await RunInvocationAsync(tool, metadata, args, connection, timeout.Token, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Error("The hub invocation timed out after " + _options.InvocationTimeout.TotalSeconds + " seconds.");
            }
            finally
            {
                connection.Application.Output.Complete();
                connection.Application.Input.Complete();

                // The dispatcher loop ends once its transport input completes; give it a moment so
                // OnDisconnectedAsync runs, but never hang a tool call on it.
                await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
            }
        }

        private async Task<McpToolInvocationResult> RunInvocationAsync(
            McpToolDescriptor tool,
            SignalRHubToolMetadata metadata,
            JsonNode?[] args,
            SyntheticHubConnectionContext connection,
            CancellationToken token,
            CancellationToken callerToken)
        {
            var output = connection.Application.Output;
            var input = connection.Application.Input;

            await SendFrameAsync(output, "{\"protocol\":\"json\",\"version\":1}", token).ConfigureAwait(false);

            using (var handshake = await ReadFrameAsync(input, token).ConfigureAwait(false))
            {
                if (handshake == null)
                {
                    return Error("The hub closed the connection during the handshake.");
                }

                if (handshake.RootElement.TryGetProperty("error", out var handshakeError))
                {
                    return Error("The hub refused the connection: " + handshakeError.GetString());
                }
            }

            var argumentsArray = new JsonArray();
            foreach (var arg in args)
            {
                argumentsArray.Add(arg?.DeepClone());
            }

            var invocation = new JsonObject
            {
                ["type"] = metadata.IsStreaming ? 4 : 1,
                ["invocationId"] = InvocationId,
                ["target"] = metadata.Method.Name,
                ["arguments"] = argumentsArray,
            };

            await SendFrameAsync(output, invocation.ToJsonString(), token).ConfigureAwait(false);

            var callerMessages = new List<JsonNode>();
            var streamItems = new JsonArray();
            var truncated = false;

            while (true)
            {
                using var frame = await ReadFrameAsync(input, token).ConfigureAwait(false);
                if (frame == null)
                {
                    return Error("The hub closed the connection before completing the invocation.");
                }

                var root = frame.RootElement;
                var type = root.TryGetProperty("type", out var typeProperty) ? typeProperty.GetInt32() : -1;

                switch (type)
                {
                    case 1: // an invocation addressed to this connection: Clients.Caller & co.
                        if (callerMessages.Count < _options.MaxCallerMessages)
                        {
                            var captured = new JsonObject
                            {
                                ["method"] = root.TryGetProperty("target", out var target) ? target.GetString() : null,
                            };
                            if (root.TryGetProperty("arguments", out var callerArguments))
                            {
                                captured["arguments"] = JsonNode.Parse(callerArguments.GetRawText());
                            }

                            callerMessages.Add(captured);
                        }
                        else
                        {
                            truncated = true;
                        }

                        break;

                    case 2: // stream item
                        if (streamItems.Count < _options.MaxStreamItems)
                        {
                            streamItems.Add(root.TryGetProperty("item", out var item) ? JsonNode.Parse(item.GetRawText()) : null);
                        }
                        else
                        {
                            truncated = true;
                            await SendFrameAsync(output, "{\"type\":5,\"invocationId\":\"" + InvocationId + "\"}", token).ConfigureAwait(false);
                            return Success(tool, metadata, null, streamItems, callerMessages, truncated: true);
                        }

                        break;

                    case 3: // completion
                        if (root.TryGetProperty("error", out var error))
                        {
                            return Error(error.GetString() ?? "The hub method failed.");
                        }

                        JsonNode? result = null;
                        if (root.TryGetProperty("result", out var resultProperty))
                        {
                            result = JsonNode.Parse(resultProperty.GetRawText());
                        }

                        return Success(tool, metadata, result, streamItems, callerMessages, truncated);

                    case 7: // close
                        var closeError = root.TryGetProperty("error", out var closeErrorProperty) ? closeErrorProperty.GetString() : null;
                        return Error(closeError == null
                            ? "The hub closed the connection before completing the invocation."
                            : "The hub closed the connection: " + closeError);

                    case 6:
                        // Echo pings back. The server aborts clients it has not heard from within
                        // ClientTimeoutInterval (default 30s); the echo keeps a long-running stream
                        // alive however high InvocationTimeout is raised.
                        await SendFrameAsync(output, "{\"type\":6}", token).ConfigureAwait(false);
                        break;

                    default: // anything newer: irrelevant to the invocation
                        break;
                }
            }
        }

        private static async Task SendFrameAsync(PipeWriter output, string json, CancellationToken token)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var buffer = new byte[payload.Length + 1];
            payload.CopyTo(buffer, 0);
            buffer[payload.Length] = RecordSeparator;
            await output.WriteAsync(buffer, token).ConfigureAwait(false);
        }

        private static async Task<JsonDocument?> ReadFrameAsync(PipeReader input, CancellationToken token)
        {
            while (true)
            {
                var result = await input.ReadAsync(token).ConfigureAwait(false);
                var buffer = result.Buffer;

                var separator = buffer.PositionOf(RecordSeparator);
                if (separator != null)
                {
                    var frame = buffer.Slice(0, separator.Value);
                    var document = JsonDocument.Parse(frame.ToArray());
                    input.AdvanceTo(buffer.GetPosition(1, separator.Value));
                    return document;
                }

                input.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted || result.IsCanceled)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// The hub-class-level requirement gates the connection: on a live connection it is endpoint
        /// metadata enforced at the HTTP negotiate, which the synthetic connection never performs.
        /// Method-level [Authorize] is deliberately NOT evaluated here - the real dispatcher does that.
        /// </summary>
        private static async Task<McpToolInvocationResult?> EvaluateConnectionGateAsync(
            SignalRHubToolMetadata metadata,
            HttpContext context)
        {
            if (metadata.ConnectionAllowsAnonymous || metadata.ConnectionAuthorizeData.Count == 0)
            {
                return null;
            }

            var policyProvider = context.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
            var policy = await AuthorizationPolicy.CombineAsync(policyProvider, metadata.ConnectionAuthorizeData).ConfigureAwait(false);
            if (policy == null)
            {
                return null;
            }

            var user = context.User ?? new ClaimsPrincipal(new ClaimsIdentity());
            var authorizationService = context.RequestServices.GetRequiredService<IAuthorizationService>();
            var result = await authorizationService.AuthorizeAsync(user, resource: null, policy).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return null;
            }

            var authenticated = user.Identity != null && user.Identity.IsAuthenticated;
            return new McpToolInvocationResult(
                authenticated ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
                "text/plain",
                "The hub requires an authorized caller. Connecting to it was refused, exactly as the negotiate request would have been.",
                new Dictionary<string, string>());
        }

        /// <summary>
        /// The tool schema describes enums by name; the hub's payload serializer may only accept
        /// numbers. Probe the configured <see cref="JsonHubProtocolOptions"/> once and convert names
        /// back to values when needed, mirroring what the core package does for HTTP bodies.
        /// </summary>
        private McpJsonCompatibility DetectCompatibility(IServiceProvider services)
        {
            var detected = _compatibility;
            if (detected != null)
            {
                return detected;
            }

            var stringEnums = false;
            try
            {
                var hubJsonOptions = services.GetService<IOptions<JsonHubProtocolOptions>>();
                if (hubJsonOptions != null)
                {
                    stringEnums = JsonSerializer.Serialize(EnumProbe.Value, hubJsonOptions.Value.PayloadSerializerOptions)
                        .StartsWith("\"", StringComparison.Ordinal);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Nabu MCP could not probe the hub JSON options; assuming numeric enums.");
            }

            detected = new McpJsonCompatibility(stringEnums);
            _compatibility = detected;
            return detected;
        }

        private enum EnumProbe
        {
            Value = 0,
        }

        private McpToolInvocationResult Success(
            McpToolDescriptor tool,
            SignalRHubToolMetadata metadata,
            JsonNode? result,
            JsonArray streamItems,
            List<JsonNode> callerMessages,
            bool truncated)
        {
            JsonNode? body;
            if (!metadata.IsStreaming && callerMessages.Count == 0 && !truncated)
            {
                body = result;
            }
            else
            {
                var wrapper = new JsonObject();
                if (metadata.IsStreaming)
                {
                    wrapper["streamItems"] = streamItems;
                }
                else
                {
                    wrapper["result"] = result;
                }

                if (callerMessages.Count > 0)
                {
                    wrapper["callerMessages"] = new JsonArray(callerMessages.ToArray());
                }

                if (truncated)
                {
                    wrapper["truncated"] = true;
                }

                body = wrapper;
            }

            if (truncated)
            {
                _logger.LogWarning(
                    "Nabu MCP truncated the result of tool {Tool}: more than {Items} stream items or {Messages} caller messages.",
                    tool.Name,
                    _options.MaxStreamItems,
                    _options.MaxCallerMessages);
            }

            return new McpToolInvocationResult(
                StatusCodes.Status200OK,
                "application/json",
                body?.ToJsonString() ?? string.Empty,
                new Dictionary<string, string>());
        }

        private static McpToolInvocationResult Error(string message)
        {
            return new McpToolInvocationResult(
                StatusCodes.Status500InternalServerError,
                "text/plain",
                message,
                new Dictionary<string, string>());
        }
    }
}
