using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Server;

namespace Nabu.Mcp.AspNetCore.Execution
{
    /// <summary>
    /// Default <see cref="IMcpToolInvoker"/>. Builds a synthetic <see cref="HttpContext"/> for the
    /// target action and pushes it through the application's own pipeline, so every filter, policy and
    /// piece of middleware behaves exactly as it would for a client-issued request.
    /// </summary>
    public class McpToolInvoker : IMcpToolInvoker
    {
        private const int MaxInvocationDepth = 4;

        private static readonly string[] NeverForwardedHeaders =
        {
            "Content-Length",
            "Content-Type",
            "Host",
            "Accept",
            "Accept-Encoding",
            "Transfer-Encoding",
            "Connection",
            "Expect",
            "Upgrade",
            McpConstants.SessionIdHeader,
            McpConstants.ProtocolVersionHeader,
            McpConstants.MethodHeader,
            McpConstants.NameHeader,
        };

        private readonly McpPipelineProvider _pipelineProvider;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly Schema.McpJsonCompatibility _compatibility;
        private readonly IHttpContextFactory? _httpContextFactory;
        private readonly IHttpContextAccessor? _httpContextAccessor;
        private readonly NabuMcpOptions _options;
        private readonly ILogger _logger;

        public McpToolInvoker(
            McpPipelineProvider pipelineProvider,
            IServiceScopeFactory scopeFactory,
            IOptions<NabuMcpOptions> options,
            Schema.McpJsonCompatibility compatibility,
            IHttpContextFactory? httpContextFactory = null,
            IHttpContextAccessor? httpContextAccessor = null,
            ILogger<McpToolInvoker>? logger = null)
        {
            _pipelineProvider = pipelineProvider ?? throw new ArgumentNullException(nameof(pipelineProvider));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _httpContextFactory = httpContextFactory;
            _httpContextAccessor = httpContextAccessor;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        public async Task<McpToolInvocationResult> InvokeAsync(
            McpToolDescriptor tool,
            JsonObject? arguments,
            HttpContext originalContext,
            CancellationToken cancellationToken)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            if (originalContext == null)
            {
                throw new ArgumentNullException(nameof(originalContext));
            }

            var depth = GetDepth(originalContext);
            if (depth >= MaxInvocationDepth)
            {
                throw new InvalidOperationException(
                    "MCP tool invocation depth limit of " + MaxInvocationDepth.ToString(CultureInfo.InvariantCulture) + " was exceeded.");
            }

            var bound = McpArgumentBinder.Bind(tool, arguments, _compatibility);

            byte[]? bodyBytes = null;
            string? bodyContentType = null;
            if (bound.IsForm)
            {
                // A form-binding endpoint always gets a form body - even an empty one - so that the
                // action's ReadFormAsync sees a well-formed request rather than a missing content type.
                bodyBytes = FormBodyEncoder.Encode(bound, out bodyContentType);
            }
            else if (bound.Body != null)
            {
                bodyBytes = Encoding.UTF8.GetBytes(bound.Body.ToJsonString());
                bodyContentType = McpConstants.JsonContentType + "; charset=utf-8";
            }

            using (var scope = _scopeFactory.CreateScope())
            using (var requestBody = bodyBytes == null ? Stream.Null : new MemoryStream(bodyBytes, writable: false))
            using (var responseBody = new CappedResponseStream(_options.MaxResponseBytes))
            {
                var features = BuildFeatures(tool, originalContext, bound, bodyBytes, bodyContentType, requestBody, responseBody, scope, cancellationToken);

                var previousAccessorContext = _httpContextAccessor?.HttpContext;
                var context = _httpContextFactory != null
                    ? _httpContextFactory.Create(features)
                    : new DefaultHttpContext(features);

                try
                {
                    if (_options.PropagateUser && originalContext.User != null)
                    {
                        context.User = originalContext.User;
                    }

                    context.Items[McpConstants.ToolInvocationItemKey] = tool.Name;
                    context.Items[McpConstants.ParentHttpContextItemKey] = originalContext;
                    context.Items[McpConstants.DepthItemKey] = depth + 1;

                    var requestUri = bound.Path + bound.QueryString;
                    _logger.LogDebug("Nabu MCP invoking tool {Tool} as {Method} {Uri}.", tool.Name, tool.HttpMethod, requestUri);

                    await _pipelineProvider.Pipeline(context).ConfigureAwait(false);

                    return ReadResult(context, responseBody, requestUri);
                }
                finally
                {
                    if (_httpContextFactory != null)
                    {
                        _httpContextFactory.Dispose(context);
                    }

                    if (_httpContextAccessor != null)
                    {
                        _httpContextAccessor.HttpContext = previousAccessorContext;
                    }
                }
            }
        }

        private static int GetDepth(HttpContext context)
        {
            object? value;
            if (context.Items != null && context.Items.TryGetValue(McpConstants.DepthItemKey, out value) && value is int depth)
            {
                return depth;
            }

            return 0;
        }

        private IFeatureCollection BuildFeatures(
            McpToolDescriptor tool,
            HttpContext originalContext,
            McpArgumentBinder.BoundRequest bound,
            byte[]? bodyBytes,
            string? bodyContentType,
            Stream requestBody,
            Stream responseBody,
            IServiceScope scope,
            CancellationToken cancellationToken)
        {
            var headers = new HeaderDictionary();
            CopyHeaders(originalContext.Request.Headers, headers);

            foreach (var header in bound.Headers)
            {
                // A model-supplied argument may never override credentials or proxy metadata; the value
                // forwarded from the MCP caller stays in place. Developer-pinned constants are exempt.
                if (_options.ProtectedHeaders.Contains(header.Key) && !bound.ConstantHeaders.Contains(header.Key))
                {
                    _logger.LogWarning(
                        "Nabu MCP dropped a model-supplied value for protected header {Header} on tool {Tool}. " +
                        "Remove the header from NabuMcpOptions.ProtectedHeaders to allow it.",
                        header.Key,
                        tool.Name);
                    continue;
                }

                headers[header.Key] = header.Value;
            }

            headers["Host"] = originalContext.Request.Host.Value;
            headers["Accept"] = McpConstants.JsonContentType;

            if (bodyBytes != null)
            {
                headers["Content-Type"] = bodyContentType ?? McpConstants.JsonContentType + "; charset=utf-8";
                headers["Content-Length"] = bodyBytes.Length.ToString(CultureInfo.InvariantCulture);
            }

            var features = new FeatureCollection();

            features.Set<IHttpRequestFeature>(new HttpRequestFeature
            {
                Protocol = originalContext.Request.Protocol,
                Scheme = originalContext.Request.Scheme,
                Method = tool.HttpMethod,
                PathBase = originalContext.Request.PathBase.HasValue ? originalContext.Request.PathBase.Value! : string.Empty,
                Path = bound.Path,
                QueryString = bound.QueryString,
                Headers = headers,
                Body = requestBody,
            });

            var responseFeature = new HttpResponseFeature { Body = responseBody };
            features.Set<IHttpResponseFeature>(responseFeature);
#if !NETSTANDARD2_0
            features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));

            // Minimal API body binding asks this feature whether a body exists at all before reading
            // the stream, and treats an absent feature as "no body". MVC never consults it.
            features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature(bodyBytes != null && bodyBytes.Length > 0));
#endif

            features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature
            {
                RequestAborted = cancellationToken,
            });

            var connection = originalContext.Features.Get<IHttpConnectionFeature>();
            if (connection != null)
            {
                features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
                {
                    ConnectionId = connection.ConnectionId,
                    LocalIpAddress = connection.LocalIpAddress,
                    LocalPort = connection.LocalPort,
                    RemoteIpAddress = connection.RemoteIpAddress,
                    RemotePort = connection.RemotePort,
                });
            }

            features.Set<IServiceProvidersFeature>(new ServiceProvidersFeature
            {
                RequestServices = scope.ServiceProvider,
            });

            return features;
        }

        private void CopyHeaders(IHeaderDictionary source, IHeaderDictionary destination)
        {
            foreach (var header in source)
            {
                if (IsForwarded(header.Key))
                {
                    destination[header.Key] = header.Value;
                }
            }
        }

        private bool IsForwarded(string name)
        {
            foreach (var forbidden in NeverForwardedHeaders)
            {
                if (string.Equals(forbidden, name, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (_options.ForwardedHeaders.Contains(name))
            {
                return true;
            }

            foreach (var prefix in _options.ForwardedHeaderPrefixes)
            {
                if (!string.IsNullOrEmpty(prefix) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

#if !NETSTANDARD2_0
        private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
        {
            public RequestBodyDetectionFeature(bool canHaveBody)
            {
                CanHaveBody = canHaveBody;
            }

            public bool CanHaveBody { get; }
        }
#endif

        private McpToolInvocationResult ReadResult(HttpContext context, CappedResponseStream responseBody, string requestUri)
        {
            // The stream discarded everything past MaxResponseBytes as it was written, so a huge
            // response never occupied more memory than the cap.
            var truncated = responseBody.Overflowed;
            var body = responseBody.ReadBufferedText();

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in context.Response.Headers)
            {
                headers[header.Key] = header.Value.ToString();
            }

            return new McpToolInvocationResult(context.Response.StatusCode, context.Response.ContentType, body, headers)
            {
                Truncated = truncated,
                RequestUri = requestUri,
            };
        }
    }
}
