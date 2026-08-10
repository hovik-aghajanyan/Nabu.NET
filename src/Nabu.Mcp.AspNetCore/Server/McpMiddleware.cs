using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Protocol;

namespace Nabu.Mcp.AspNetCore.Server
{
    /// <summary>
    /// Serves the MCP endpoint over the Streamable HTTP transport: JSON-RPC in, JSON-RPC out.
    /// </summary>
    public class McpMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly NabuMcpOptions _options;
        private readonly ILogger _logger;

        public McpMiddleware(
            RequestDelegate next,
            IOptions<NabuMcpOptions> options,
            ILogger<McpMiddleware>? logger = null)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        public async Task InvokeAsync(HttpContext context, McpRequestHandler handler)
        {
            if (!IsMcpRequest(context))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var method = context.Request.Method.ToUpperInvariant();

            // When anonymous access is closed every request is gated up front. When it is open, a
            // POST is gated after it has been parsed, because which JSON-RPC methods it carries is
            // what decides whether credentials are needed at all - and the remaining verbs stay
            // open too: answering an anonymous GET with 401 instead of the spec's 405 sends clients
            // into an OAuth discovery flow this server does not offer.
            var gateHere = _options.RequireAuthorization &&
                           _options.AnonymousAccess == McpAnonymousAccess.None;

            if (gateHere && !await AuthorizeAsync(context).ConfigureAwait(false))
            {
                return;
            }

            switch (method)
            {
                case "POST":
                    await HandlePostAsync(context, handler).ConfigureAwait(false);
                    return;

                case "DELETE":
                    // Session teardown. This server is stateless, so there is nothing to release.
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    return;

                case "OPTIONS":
                    context.Response.StatusCode = StatusCodes.Status204NoContent;
                    context.Response.Headers["Allow"] = "POST, DELETE, OPTIONS";
                    return;

                default:
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    context.Response.Headers["Allow"] = "POST, DELETE, OPTIONS";
                    await WriteJsonAsync(
                        context,
                        JsonRpc.Error(null, McpConstants.InvalidRequest, "This MCP endpoint only accepts POST requests."))
                        .ConfigureAwait(false);
                    return;
            }
        }

        private bool IsMcpRequest(HttpContext context)
        {
            var path = _options.Path;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            return context.Request.Path.StartsWithSegments(
                new PathString(path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path),
                StringComparison.OrdinalIgnoreCase,
                out var remaining) && (!remaining.HasValue || remaining == "/");
        }

        private async Task<bool> AuthorizeAsync(HttpContext context)
        {
            var policyProvider = context.RequestServices.GetService(typeof(IAuthorizationPolicyProvider)) as IAuthorizationPolicyProvider;
            var authorizationService = context.RequestServices.GetService(typeof(IAuthorizationService)) as IAuthorizationService;

            if (policyProvider == null || authorizationService == null)
            {
                throw new InvalidOperationException(
                    "NabuMcpOptions.RequireAuthorization is enabled but ASP.NET Core authorization services are not registered. " +
                    "Call services.AddAuthorization().");
            }

            var policy = string.IsNullOrEmpty(_options.AuthorizationPolicy)
                ? await policyProvider.GetDefaultPolicyAsync().ConfigureAwait(false)
                : await policyProvider.GetPolicyAsync(_options.AuthorizationPolicy!).ConfigureAwait(false);

            if (policy == null)
            {
                throw new InvalidOperationException(
                    "Authorization policy '" + _options.AuthorizationPolicy + "' was not found.");
            }

            var result = await authorizationService.AuthorizeAsync(context.User, context, policy.Requirements).ConfigureAwait(false);
            if (result.Succeeded)
            {
                return true;
            }

            var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;
            if (isAuthenticated)
            {
                await ForbidAsync(context).ConfigureAwait(false);
            }
            else
            {
                await ChallengeAsync(context).ConfigureAwait(false);
            }

            return false;
        }

        /// <summary>
        /// Whether the caller presented credentials that were rejected. The anonymous door is for clients
        /// that hold none yet; a caller whose token was refused is challenged instead, so that a stale or
        /// malformed token is never mistaken for a deliberate anonymous request.
        /// </summary>
        private async Task<bool> HasRejectedCredentialsAsync(HttpContext context)
        {
            if (context.RequestServices?.GetService(typeof(IAuthenticationService)) == null)
            {
                return false;
            }

            try
            {
                if (_options.AuthenticationSchemes.Count == 0)
                {
                    var result = await context.AuthenticateAsync().ConfigureAwait(false);
                    return result?.Failure != null;
                }

                foreach (var scheme in _options.AuthenticationSchemes)
                {
                    var result = await context.AuthenticateAsync(scheme).ConfigureAwait(false);
                    if (result?.Failure != null)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (InvalidOperationException ex)
            {
                // No default scheme, or no such scheme. Nothing was rejected because nothing was tried.
                _logger.LogDebug(ex, "Nabu MCP could not inspect the caller's credentials before answering anonymously.");
                return false;
            }
        }

        private async Task ChallengeAsync(HttpContext context)
        {
            if (_options.AuthenticationSchemes.Count == 0)
            {
                await context.ChallengeAsync().ConfigureAwait(false);
                return;
            }

            foreach (var scheme in _options.AuthenticationSchemes)
            {
                await context.ChallengeAsync(scheme).ConfigureAwait(false);
            }
        }

        private async Task ForbidAsync(HttpContext context)
        {
            if (_options.AuthenticationSchemes.Count == 0)
            {
                await context.ForbidAsync().ConfigureAwait(false);
                return;
            }

            foreach (var scheme in _options.AuthenticationSchemes)
            {
                await context.ForbidAsync(scheme).ConfigureAwait(false);
            }
        }

        private async Task HandlePostAsync(HttpContext context, McpRequestHandler handler)
        {
            JsonNode? payload;
            try
            {
                payload = await ReadPayloadAsync(context).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(context, JsonRpc.Error(null, McpConstants.ParseError, "Invalid JSON: " + ex.Message))
                    .ConfigureAwait(false);
                return;
            }

            bool isBatch;
            IReadOnlyList<JsonRpcRequest> requests;
            try
            {
                requests = McpRequestHandler.ParseRequests(payload, out isBatch);
            }
            catch (McpInvalidRequestException ex)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(context, JsonRpc.Error(null, McpConstants.InvalidRequest, ex.Message)).ConfigureAwait(false);
                return;
            }

            if (_options.RequireAuthorization && _options.AnonymousAccess != McpAnonymousAccess.None)
            {
                var needsAuthorization =
                    await handler.RequiresEndpointAuthorizationAsync(requests, context, context.RequestAborted).ConfigureAwait(false) ||
                    await HasRejectedCredentialsAsync(context).ConfigureAwait(false);

                if (needsAuthorization && !await AuthorizeAsync(context).ConfigureAwait(false))
                {
                    return;
                }
            }

            // A single request that declares its protocol version in _meta (or calls the
            // handshake-free server/discover) is served under revision 2026-07-28 semantics.
            // Batches stay on the legacy path: modern transports post one message at a time.
            if (!isBatch && requests.Count == 1 && ModernProtocol.IsModern(requests[0]))
            {
                await HandleModernPostAsync(context, handler, requests[0]).ConfigureAwait(false);
                return;
            }

            var responses = new JsonArray();
            var hasInitialize = false;

            foreach (var request in requests)
            {
                if (string.Equals(request.Method, "initialize", StringComparison.Ordinal))
                {
                    hasInitialize = true;
                }

                var response = await handler.HandleAsync(request, context, context.RequestAborted).ConfigureAwait(false);
                if (response != null)
                {
                    responses.Add(response);
                }
            }

            if (hasInitialize && !context.Response.Headers.ContainsKey(McpConstants.SessionIdHeader))
            {
                context.Response.Headers[McpConstants.SessionIdHeader] = Guid.NewGuid().ToString("N");
            }

            if (responses.Count == 0)
            {
                // Notifications only: acknowledge without a body, as the transport requires.
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            JsonNode result;
            if (isBatch)
            {
                result = responses;
            }
            else
            {
                result = responses[0]!;
                responses.RemoveAt(0);
            }

            if (PrefersEventStream(context))
            {
                await WriteEventStreamAsync(context, result).ConfigureAwait(false);
            }
            else
            {
                await WriteJsonAsync(context, result).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Serves one revision 2026-07-28 request: validates the mirrored headers and the declared
        /// protocol version, then dispatches. No session id is ever minted on this path.
        /// </summary>
        private async Task HandleModernPostAsync(HttpContext context, McpRequestHandler handler, JsonRpcRequest request)
        {
            if (request.IsNotification)
            {
                // Header requirements for notification POSTs are not defined by this revision;
                // the transport just wants them acknowledged without a body.
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                return;
            }

            var declaredVersion = ModernProtocol.DeclaredVersion(request);

            var headerError = ModernProtocol.ValidateHeaders(context, request, declaredVersion);
            if (headerError != null)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(context, JsonRpc.Error(request.Id, McpConstants.HeaderMismatch, headerError))
                    .ConfigureAwait(false);
                return;
            }

            if (!ModernProtocol.IsSupportedVersion(declaredVersion!))
            {
                var supported = new JsonArray();
                foreach (var version in McpConstants.ModernProtocolVersions)
                {
                    supported.Add(version);
                }

                var data = new JsonObject
                {
                    ["supported"] = supported,
                    ["requested"] = declaredVersion,
                };

                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await WriteJsonAsync(
                    context,
                    JsonRpc.Error(request.Id, McpConstants.UnsupportedProtocolVersion, "Unsupported protocol version", data))
                    .ConfigureAwait(false);
                return;
            }

            var response = await handler.HandleModernAsync(request, context, context.RequestAborted).ConfigureAwait(false);
            context.Response.StatusCode = response.StatusCode;

            if (PrefersEventStream(context))
            {
                await WriteEventStreamAsync(context, response.Body).ConfigureAwait(false);
            }
            else
            {
                await WriteJsonAsync(context, response.Body).ConfigureAwait(false);
            }
        }

        private static async Task<JsonNode?> ReadPayloadAsync(HttpContext context)
        {
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
            {
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new JsonException("The request body was empty.");
                }

                return JsonNode.Parse(text);
            }
        }

        private static bool PrefersEventStream(HttpContext context)
        {
            var accept = context.Request.Headers["Accept"].ToString();
            if (string.IsNullOrEmpty(accept))
            {
                return false;
            }

            var acceptsJson = accept.IndexOf(McpConstants.JsonContentType, StringComparison.OrdinalIgnoreCase) >= 0 ||
                              accept.IndexOf("*/*", StringComparison.Ordinal) >= 0;
            var acceptsStream = accept.IndexOf(McpConstants.EventStreamContentType, StringComparison.OrdinalIgnoreCase) >= 0;

            return acceptsStream && !acceptsJson;
        }

        private static Task WriteJsonAsync(HttpContext context, JsonNode payload)
        {
            context.Response.ContentType = McpConstants.JsonContentType + "; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            context.Response.ContentLength = bytes.Length;
            return context.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        }

        private static async Task WriteEventStreamAsync(HttpContext context, JsonNode payload)
        {
            context.Response.ContentType = McpConstants.EventStreamContentType;
            context.Response.Headers["Cache-Control"] = "no-cache";

            var text = "event: message\ndata: " + payload.ToJsonString() + "\n\n";
            var bytes = Encoding.UTF8.GetBytes(text);
            await context.Response.Body.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            await context.Response.Body.FlushAsync().ConfigureAwait(false);
        }
    }
}
