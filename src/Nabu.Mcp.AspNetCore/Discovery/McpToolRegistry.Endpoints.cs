#if !NETSTANDARD2_0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Nabu.Mcp.AspNetCore.Schema;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>
    /// The Minimal API half of the registry: discovers tools from route-handler endpoints
    /// (<c>app.MapGet(...).McpTool()</c>). Not available on the netstandard2.0 target, which builds
    /// against ASP.NET Core 2.x - endpoint routing does not exist there.
    /// </summary>
    public partial class McpToolRegistry
    {
        private readonly EndpointDataSource? _endpointDataSource;
        private readonly IServiceProviderIsService? _serviceProviderIsService;

        /// <summary>Bumped whenever the endpoint data source reports a change, to invalidate the cache.</summary>
        private int _endpointVersion;

        /// <remarks>
        /// <paramref name="actionProvider"/> may be <c>null</c> in an application that hosts no
        /// controllers, and <paramref name="endpointDataSource"/> may be <c>null</c> to skip Minimal
        /// API discovery. <paramref name="serviceProviderIsService"/> is used the same way Minimal
        /// APIs use it: a handler parameter resolvable from the container is bound from services, not
        /// from the request, so it is not a tool input.
        /// </remarks>
        public McpToolRegistry(
            IActionDescriptorCollectionProvider? actionProvider,
            IOptions<NabuMcpOptions> options,
            JsonSchemaGenerator schemaGenerator,
            IXmlDocumentationProvider documentation,
            EndpointDataSource? endpointDataSource,
            IServiceProviderIsService? serviceProviderIsService,
            ILogger<McpToolRegistry>? logger)
            : this(actionProvider, options, schemaGenerator, documentation, logger)
        {
            _endpointDataSource = endpointDataSource;
            _serviceProviderIsService = serviceProviderIsService;

            if (endpointDataSource != null)
            {
                // Endpoints mapped after the first tools/list (or conditionally at startup) must not be
                // served from a stale cache; the registry is a singleton, so the subscription lives for
                // the application's lifetime.
                ChangeToken.OnChange(endpointDataSource.GetChangeToken, () => Interlocked.Increment(ref _endpointVersion));
            }
        }

        private void BuildEndpointTools(List<McpToolDescriptor> results, ISet<string> usedNames)
        {
            var source = _endpointDataSource;
            if (source == null)
            {
                return;
            }

            foreach (var endpoint in source.Endpoints)
            {
                if (!(endpoint is RouteEndpoint routeEndpoint))
                {
                    continue;
                }

                List<McpToolDescriptor> tools;
                try
                {
                    tools = CreateEndpointTools(routeEndpoint, usedNames);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Nabu MCP could not expose the endpoint {Endpoint} as a tool.",
                        routeEndpoint.DisplayName ?? routeEndpoint.RoutePattern.RawText);
                    continue;
                }

                foreach (var tool in tools)
                {
                    if (_options.ToolFilter != null && !_options.ToolFilter(tool))
                    {
                        continue;
                    }

                    results.Add(tool);
                }
            }
        }

        private List<McpToolDescriptor> CreateEndpointTools(RouteEndpoint endpoint, ISet<string> usedNames)
        {
            var empty = new List<McpToolDescriptor>();
            var metadata = endpoint.Metadata;

            // Controller actions and Razor pages carry an ActionDescriptor and are covered by the MVC
            // discovery path; listing them here would duplicate every tool.
            if (metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor>() != null)
            {
                return empty;
            }

            if (metadata.GetMetadata<McpIgnoreAttribute>() != null)
            {
                return empty;
            }

            // Route handlers publish their handler's MethodInfo into the endpoint metadata, and an
            // attribute applied to the handler lambda lands there too - so both `.McpTool()` and
            // `app.MapGet("/x", [McpTool] (int id) => ...)` are found by the same lookup.
            var method = metadata.GetMetadata<MethodInfo>();
            var variants = new List<McpToolAttribute?>(metadata.GetOrderedMetadata<McpToolAttribute>());

            if (variants.Count == 0)
            {
                // Blanket exposure covers delegate route handlers only - other endpoint kinds (hubs,
                // health checks, ...) have no parameter surface to describe - and respects the API
                // description opt-out, mirroring what ExposeAllActions does for controllers.
                if (!_options.ExposeAllActions || method == null)
                {
                    return empty;
                }

                if (metadata.GetMetadata<IExcludeFromDescriptionMetadata>()?.ExcludeFromDescription == true)
                {
                    return empty;
                }

                variants.Add(null);
            }

            var routeTemplate = endpoint.RoutePattern.RawText;
            if (string.IsNullOrEmpty(routeTemplate))
            {
                _logger.LogWarning(
                    "Nabu MCP skipped the endpoint {Endpoint}: its route pattern has no textual template.",
                    endpoint.DisplayName);
                return empty;
            }

            routeTemplate = RouteTemplateHelper.Normalize(routeTemplate!);

            if (method == null)
            {
                _logger.LogWarning(
                    "Nabu MCP skipped the endpoint {Endpoint}: it carries [McpTool] metadata but no handler " +
                    "MethodInfo, so its inputs cannot be inferred. Only delegate route handlers are supported.",
                    endpoint.DisplayName ?? routeTemplate);
                return empty;
            }

            var allParameters = new List<McpToolParameterDescriptor>();
            string httpMethod;
            var display = BuildEndpointDisplay(endpoint, metadata, routeTemplate, out httpMethod);

            if (!TryBuildEndpointParameters(method, routeTemplate, display, allParameters))
            {
                return empty;
            }

            // Map() without verbs: mirror the controller inference - POST when something binds from
            // the body, GET otherwise.
            if (httpMethod.Length == 0)
            {
                httpMethod = allParameters.Any(p => p.Source == McpParameterSource.Body) ? "POST" : "GET";
                display = httpMethod + " /" + routeTemplate;
            }

            if (variants.Count > 1 && variants.Count(v => string.IsNullOrEmpty(v?.Name)) > 1)
            {
                _logger.LogWarning(
                    "Nabu MCP found {Count} [McpTool] occurrences on {Endpoint} without an explicit Name. " +
                    "Give each variant a name, otherwise all but the first are published under a generated '_2', '_3', ... suffix.",
                    variants.Count,
                    display);
            }

            var authorization = ResolveEndpointAuthorization(metadata);
            var namingContext = BuildEndpointNamingContext(metadata, method, httpMethod, routeTemplate);
            var fallbackTitle = httpMethod + " /" + routeTemplate;
            var results = new List<McpToolDescriptor>(variants.Count);

            foreach (var attribute in variants)
            {
                if (attribute != null && !attribute.Enabled)
                {
                    continue;
                }

                List<McpToolParameterDescriptor> parameters;
                List<McpToolConstantDescriptor> constants;
                if (!TryApplyVariant(display, attribute, routeTemplate, allParameters, out parameters, out constants))
                {
                    continue;
                }

                var name = string.IsNullOrEmpty(attribute?.Name)
                    ? ReserveName(_options.ToolNameFactory(namingContext), usedNames, display)
                    : ReserveName(attribute!.Name!, usedNames, display);

                var inputSchema = BuildInputSchema(parameters);
                var annotations = BuildAnnotations(attribute, attribute, httpMethod, fallbackTitle);

                results.Add(new McpToolDescriptor(name, httpMethod, routeTemplate, parameters, inputSchema, annotations)
                {
                    Description = ResolveEndpointDescription(attribute, method, httpMethod, routeTemplate),
                    Constants = constants,
                    Authorization = authorization,
                    Endpoint = endpoint,
                });
            }

            return results;
        }

        private static string BuildEndpointDisplay(
            RouteEndpoint endpoint,
            EndpointMetadataCollection metadata,
            string routeTemplate,
            out string httpMethod)
        {
            httpMethod = string.Empty;

            var methods = metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods != null && methods.Count > 0)
            {
                // Prefer a verb that is unambiguous for a tool call, as the controller path does.
                foreach (var preferred in new[] { "POST", "PUT", "PATCH", "DELETE", "GET" })
                {
                    if (methods.Any(m => string.Equals(m, preferred, StringComparison.OrdinalIgnoreCase)))
                    {
                        httpMethod = preferred;
                        break;
                    }
                }

                if (httpMethod.Length == 0)
                {
                    httpMethod = methods[0].ToUpperInvariant();
                }
            }

            return httpMethod.Length > 0 ? httpMethod + " /" + routeTemplate : "/" + routeTemplate;
        }

        /// <summary>
        /// Reads the authorization metadata endpoint routing would enforce for this endpoint:
        /// <c>[Authorize]</c> data added by attributes or <c>RequireAuthorization()</c>, policy
        /// instances contributed directly, and <c>[AllowAnonymous]</c>, which wins outright.
        /// </summary>
        private static McpToolAuthorization ResolveEndpointAuthorization(EndpointMetadataCollection metadata)
        {
            var allowAnonymous = metadata.GetMetadata<IAllowAnonymous>() != null;
            var authorizeData = new List<IAuthorizeData>(metadata.GetOrderedMetadata<IAuthorizeData>());
            var policies = new List<AuthorizationPolicy>(metadata.GetOrderedMetadata<AuthorizationPolicy>());
            return new McpToolAuthorization(allowAnonymous, authorizeData, policies);
        }

        /// <summary>
        /// A route handler has no controller/action pair, so the naming context is synthesized: the
        /// endpoint name (<c>.WithName(...)</c>) or the handler method's own name when it has a real
        /// one, otherwise the verb plus the route tokens - so <c>GET /customers/{id}</c> becomes
        /// <c>customers_get_by_id</c> under the default factory.
        /// </summary>
        private static McpToolNamingContext BuildEndpointNamingContext(
            EndpointMetadataCollection metadata,
            MethodInfo method,
            string httpMethod,
            string routeTemplate)
        {
            var endpointName = metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
            if (!string.IsNullOrEmpty(endpointName))
            {
                return new McpToolNamingContext(string.Empty, endpointName!, httpMethod, routeTemplate);
            }

            var literals = new List<string>();
            foreach (var segment in routeTemplate.Split('/'))
            {
                if (segment.Length > 0 && segment.IndexOf('{') < 0)
                {
                    literals.Add(segment);
                }
            }

            var controllerName = string.Join("_", literals);

            // A method-group handler has a meaningful name; a lambda's compiler-generated one does not.
            if (method.Name.IndexOf('<') < 0 && !method.Name.StartsWith("lambda_method", StringComparison.Ordinal))
            {
                return new McpToolNamingContext(controllerName, method.Name, httpMethod, routeTemplate);
            }

            var tokens = RouteTemplateHelper.GetTokenNames(routeTemplate);
            var actionName = httpMethod.ToLowerInvariant();
            if (tokens.Count > 0)
            {
                actionName += "_by_" + string.Join("_", tokens);
            }

            return new McpToolNamingContext(controllerName, actionName, httpMethod, routeTemplate);
        }

        private string? ResolveEndpointDescription(
            McpToolAttribute? attribute,
            MethodInfo method,
            string httpMethod,
            string routeTemplate)
        {
            if (!string.IsNullOrEmpty(attribute?.Description))
            {
                return attribute!.Description;
            }

            // Method-group handlers can carry XML documentation; lambdas cannot.
            var summary = _documentation.GetSummary(method);
            if (!string.IsNullOrEmpty(summary))
            {
                var returns = _documentation.GetReturnsDescription(method);
                return string.IsNullOrEmpty(returns) ? summary : summary + " Returns: " + returns;
            }

            return "Invokes " + httpMethod + " /" + routeTemplate + ".";
        }

        private bool TryBuildEndpointParameters(
            MethodInfo method,
            string routeTemplate,
            string display,
            List<McpToolParameterDescriptor> parameters)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var parameterInfo in method.GetParameters())
            {
                var type = parameterInfo.ParameterType;
                var attributes = parameterInfo.GetCustomAttributes().ToList();

                if (attributes.Any(a => a is McpIgnoreAttribute))
                {
                    continue;
                }

                if (IsIgnoredType(type) || typeof(System.IO.Pipelines.PipeReader).IsAssignableFrom(type))
                {
                    continue;
                }

                if (attributes.Any(a => a is IFromServiceMetadata || a is FromKeyedServicesAttribute))
                {
                    continue;
                }

                if (IsFormType(type) || attributes.Any(a => a is IFromFormMetadata))
                {
                    _logger.LogWarning(
                        "Nabu MCP skipped {Endpoint}: parameter '{Parameter}' binds form data, which tools cannot supply.",
                        display,
                        parameterInfo.Name);
                    return false;
                }

                if (attributes.Any(a => a is AsParametersAttribute))
                {
                    _logger.LogWarning(
                        "Nabu MCP skipped {Endpoint}: [AsParameters] on '{Parameter}' is not supported yet. " +
                        "Bind the values as individual handler parameters to expose this endpoint as a tool.",
                        display,
                        parameterInfo.Name);
                    return false;
                }

                var bindingName = parameterInfo.Name ?? string.Empty;
                McpParameterSource source;

                var fromRoute = attributes.OfType<IFromRouteMetadata>().FirstOrDefault();
                var fromQuery = attributes.OfType<IFromQueryMetadata>().FirstOrDefault();
                var fromHeader = attributes.OfType<IFromHeaderMetadata>().FirstOrDefault();
                var fromBody = attributes.OfType<IFromBodyMetadata>().FirstOrDefault();

                if (fromRoute != null)
                {
                    source = McpParameterSource.Route;
                    bindingName = fromRoute.Name ?? bindingName;
                }
                else if (fromQuery != null)
                {
                    source = McpParameterSource.Query;
                    bindingName = fromQuery.Name ?? bindingName;
                }
                else if (fromHeader != null)
                {
                    if (!_options.ExposeHeaderParameters)
                    {
                        continue;
                    }

                    source = McpParameterSource.Header;
                    bindingName = fromHeader.Name ?? bindingName;
                }
                else if (fromBody != null)
                {
                    source = McpParameterSource.Body;
                }
                else if (RouteTemplateHelper.ContainsToken(routeTemplate, bindingName))
                {
                    source = McpParameterSource.Route;
                }
                else if (!JsonSchemaGenerator.IsComplexObject(type) &&
                         !HasComplexElementType(type))
                {
                    // Strings, primitives, parsables and their collections bind from the query string.
                    source = McpParameterSource.Query;
                }
                else if (_serviceProviderIsService != null && _serviceProviderIsService.IsService(type))
                {
                    // Minimal APIs bind a parameter the container can resolve from services, so it is
                    // not part of the endpoint's request surface.
                    continue;
                }
                else if (HasBindAsync(type))
                {
                    // The framework materializes these from the HttpContext itself; a tool cannot and
                    // need not supply them.
                    continue;
                }
                else if (HasTryParse(type))
                {
                    source = McpParameterSource.Query;
                }
                else
                {
                    // Unlike MVC, Minimal APIs infer the body for complex types on every verb.
                    source = McpParameterSource.Body;
                }

                AddParameter(
                    method,
                    parameterInfo.Name ?? bindingName,
                    bindingName,
                    type,
                    parameterInfo,
                    source,
                    routeTemplate,
                    parameters,
                    seen,
                    valueTypesDefaultToOptional: false);
            }

            return true;
        }

        /// <summary>
        /// True for collections whose elements are complex objects - those bind from the JSON body in
        /// Minimal APIs, while collections of simple values bind from repeated query keys.
        /// </summary>
        private static bool HasComplexElementType(Type type)
        {
            var element = JsonSchemaGenerator.GetEnumerableElementType(type);
            return element != null && JsonSchemaGenerator.IsComplexObject(element);
        }

        private static bool HasBindAsync(Type type)
        {
            foreach (var candidate in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (!string.Equals(candidate.Name, "BindAsync", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = candidate.GetParameters();
                if (parameters.Length >= 1 && parameters.Length <= 2 &&
                    parameters[0].ParameterType == typeof(HttpContext))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasTryParse(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            foreach (var candidate in underlying.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (!string.Equals(candidate.Name, "TryParse", StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = candidate.GetParameters();
                if (parameters.Length >= 2 &&
                    parameters[0].ParameterType == typeof(string) &&
                    parameters[parameters.Length - 1].IsOut)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
#endif
