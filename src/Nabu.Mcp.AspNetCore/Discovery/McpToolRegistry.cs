using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Schema;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>
    /// Discovers MCP tools from the MVC action table and - on modern frameworks - from Minimal API
    /// route handlers. The result is cached and rebuilt automatically whenever the action descriptor
    /// collection or the endpoint data source changes (for example when application parts are added).
    /// </summary>
    public partial class McpToolRegistry : IMcpToolRegistry
    {
        private static readonly Type[] IgnoredParameterTypes =
        {
            typeof(CancellationToken),
            typeof(HttpContext),
            typeof(HttpRequest),
            typeof(HttpResponse),
            typeof(System.Security.Claims.ClaimsPrincipal),
            typeof(Stream),
        };

        /// <summary>How a parameter participates in form binding, if it does at all.</summary>
        internal enum FormParameterKind
        {
            None,

            /// <summary>A single <see cref="IFormFile"/>.</summary>
            File,

            /// <summary>An <see cref="IFormFileCollection"/> or any collection of <see cref="IFormFile"/>.</summary>
            FileCollection,

            /// <summary>The whole form, bound as <see cref="IFormCollection"/>.</summary>
            FormCollection,
        }

        private static readonly IMcpToolSource[] NoToolSources = new IMcpToolSource[0];

        private readonly IActionDescriptorCollectionProvider? _actionProvider;
        private readonly NabuMcpOptions _options;
        private readonly JsonSchemaGenerator _schemaGenerator;
        private readonly IXmlDocumentationProvider _documentation;
        private readonly ILogger _logger;
        private readonly IReadOnlyList<IMcpToolSource> _toolSources;

        private readonly object _sync = new object();
        private long _cachedVersion = -1;
        private IReadOnlyList<McpToolDescriptor>? _tools;
        private IReadOnlyDictionary<string, McpToolDescriptor>? _byName;

        /// <remarks>
        /// <paramref name="actionProvider"/> may be <c>null</c> in an application that hosts no
        /// controllers, in which case only Minimal API endpoints are discovered.
        /// </remarks>
        public McpToolRegistry(
            IActionDescriptorCollectionProvider? actionProvider,
            IOptions<NabuMcpOptions> options,
            JsonSchemaGenerator schemaGenerator,
            IXmlDocumentationProvider documentation,
            ILogger<McpToolRegistry>? logger = null,
            IEnumerable<IMcpToolSource>? toolSources = null)
        {
            _actionProvider = actionProvider;
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _schemaGenerator = schemaGenerator ?? throw new ArgumentNullException(nameof(schemaGenerator));
            _documentation = documentation ?? NullXmlDocumentationProvider.Instance;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
            _toolSources = toolSources as IReadOnlyList<IMcpToolSource> ?? toolSources?.ToList() ?? (IReadOnlyList<IMcpToolSource>)NoToolSources;
        }

        public IReadOnlyList<McpToolDescriptor> GetTools()
        {
            EnsureBuilt();
            return _tools!;
        }

        public bool TryGetTool(string name, [NotNullWhen(true)] out McpToolDescriptor? tool)
        {
            EnsureBuilt();
            if (name == null)
            {
                tool = null;
                return false;
            }

            return _byName!.TryGetValue(name, out tool) && tool != null;
        }

        /// <summary>
        /// A single number that changes whenever either discovery source changes: the MVC action table
        /// version in the upper half, the endpoint change counter in the lower half.
        /// </summary>
        private long CurrentVersion()
        {
            long version = _actionProvider != null ? _actionProvider.ActionDescriptors.Version : 0;
            version <<= 32;
#if !NETSTANDARD2_0
            version |= (uint)Volatile.Read(ref _endpointVersion);
#endif
            return version;
        }

        private void EnsureBuilt()
        {
            if (_tools != null && _cachedVersion == CurrentVersion())
            {
                return;
            }

            lock (_sync)
            {
                var version = CurrentVersion();
                if (_tools != null && _cachedVersion == version)
                {
                    return;
                }

                var actions = _actionProvider?.ActionDescriptors.Items
                              ?? (IReadOnlyList<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor>)
                                  new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor[0];

                var tools = Build(actions);
                var byName = new Dictionary<string, McpToolDescriptor>(StringComparer.Ordinal);
                foreach (var tool in tools)
                {
                    byName[tool.Name] = tool;
                }

                _tools = tools;
                _byName = byName;
                _cachedVersion = version;

                _logger.LogInformation("Nabu MCP discovered {ToolCount} tool(s).", tools.Count);
            }
        }

        private IReadOnlyList<McpToolDescriptor> Build(IReadOnlyList<Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor> actions)
        {
            var results = new List<McpToolDescriptor>();
            var used = new HashSet<string>(StringComparer.Ordinal);

            foreach (var descriptor in actions)
            {
                if (!(descriptor is ControllerActionDescriptor action))
                {
                    continue;
                }

                List<McpToolDescriptor> tools;
                try
                {
                    tools = CreateTools(action, used);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Nabu MCP could not expose {Controller}.{Action} as a tool.",
                        action.ControllerName,
                        action.ActionName);
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

#if !NETSTANDARD2_0
            BuildEndpointTools(results, used);
#endif

            BuildSourceTools(results, used);

            results.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return results;
        }

        /// <summary>
        /// Appends the tools contributed by registered <see cref="IMcpToolSource"/> implementations.
        /// A source owns its own naming; a name already claimed by an HTTP tool or an earlier source
        /// is skipped with a warning rather than renamed, because the descriptor's identity - and any
        /// metadata its source attached to it - must survive intact.
        /// </summary>
        private void BuildSourceTools(List<McpToolDescriptor> results, ISet<string> usedNames)
        {
            foreach (var source in _toolSources)
            {
                IReadOnlyList<McpToolDescriptor> tools;
                try
                {
                    tools = source.GetTools();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Nabu MCP tool source {Source} failed to contribute tools.", source.GetType().FullName);
                    continue;
                }

                foreach (var tool in tools)
                {
                    if (_options.ToolFilter != null && !_options.ToolFilter(tool))
                    {
                        continue;
                    }

                    if (!usedNames.Add(tool.Name))
                    {
                        _logger.LogWarning(
                            "Nabu MCP tool source {Source} contributed '{Tool}', but that name is already taken; the tool is skipped. Give it a unique name.",
                            source.GetType().FullName,
                            tool.Name);
                        continue;
                    }

                    results.Add(tool);
                }
            }
        }

        /// <summary>
        /// Builds every tool an action contributes. An action carrying several <see cref="McpToolAttribute"/>
        /// occurrences yields one tool per occurrence, each with its own name and parameter set.
        /// </summary>
        private List<McpToolDescriptor> CreateTools(ControllerActionDescriptor action, ISet<string> usedNames)
        {
            var empty = new List<McpToolDescriptor>();
            var method = action.MethodInfo;
            var controllerType = action.ControllerTypeInfo;

            if (method.GetCustomAttribute<McpIgnoreAttribute>() != null ||
                controllerType.GetCustomAttribute<McpIgnoreAttribute>() != null)
            {
                return empty;
            }

            var methodAttributes = method.GetCustomAttributes<McpToolAttribute>(inherit: true).ToList();
            var controllerAttributes = controllerType.GetCustomAttributes<McpToolAttribute>(inherit: true).ToList();
            var controllerAttribute = controllerAttributes.Count > 0 ? controllerAttributes[0] : null;

            // A controller-wide [McpTool] is a default for the actions that carry none of their own; an
            // action that declares its own variants replaces it rather than adding to it.
            var isMethodLevel = methodAttributes.Count > 0;
            var variants = new List<McpToolAttribute?>();
            variants.AddRange(isMethodLevel ? methodAttributes : controllerAttributes);

            if (variants.Count == 0)
            {
                if (!_options.ExposeAllActions)
                {
                    return empty;
                }

                // Blanket exposure still respects the API explorer opt-out.
                var apiExplorer = method.GetCustomAttribute<ApiExplorerSettingsAttribute>()
                                  ?? controllerType.GetCustomAttribute<ApiExplorerSettingsAttribute>();
                if (apiExplorer != null && apiExplorer.IgnoreApi)
                {
                    return empty;
                }

                variants.Add(null);
            }

            if (variants.Count > 1 && variants.Count(v => string.IsNullOrEmpty(v?.Name)) > 1)
            {
                _logger.LogWarning(
                    "Nabu MCP found {Count} [McpTool] attributes on {Controller}.{Action} without an explicit Name. " +
                    "Give each variant a name, otherwise all but the first are published under a generated '_2', '_3', ... suffix.",
                    variants.Count,
                    action.ControllerName,
                    action.ActionName);
            }

            var display = action.ControllerName + "." + action.ActionName;
            var httpMethod = ResolveHttpMethod(action);
            var routeTemplate = ResolveRouteTemplate(action);
            if (routeTemplate == null)
            {
                _logger.LogWarning(
                    "Nabu MCP skipped {Controller}.{Action}: no route template could be resolved. Attribute routing is required.",
                    action.ControllerName,
                    action.ActionName);
                return empty;
            }

            var allParameters = new List<McpToolParameterDescriptor>();
            if (!TryBuildParameters(action, httpMethod, routeTemplate, allParameters))
            {
                return empty;
            }

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

                var methodAttribute = isMethodLevel ? attribute : null;
                var inputSchema = BuildInputSchema(parameters);
                var annotations = BuildAnnotations(attribute, methodAttribute, httpMethod, Humanize(action.ActionName));
                var name = ResolveName(action, methodAttribute, httpMethod, routeTemplate, usedNames);

                results.Add(new McpToolDescriptor(name, httpMethod, routeTemplate, action, parameters, inputSchema, annotations)
                {
                    Description = ResolveDescription(action, methodAttribute, controllerAttribute, httpMethod, routeTemplate),
                    ConstantRouteValues = new Dictionary<string, string?>(action.RouteValues, StringComparer.OrdinalIgnoreCase),
                    Constants = constants,
                    Authorization = ResolveAuthorization(action),
                });
            }

            return results;
        }

        /// <summary>
        /// Reads the authorization an action demands, so <c>tools/list</c> can be tailored to the caller.
        /// </summary>
        /// <remarks>
        /// Three sources are merged, because which one carries the metadata depends on the ASP.NET Core
        /// version and on how the application is wired: attributes on the action, attributes on the
        /// controller, and the action's filter descriptors - which is where globally registered
        /// authorization filters show up. As in MVC, an <c>[AllowAnonymous]</c> anywhere in that set wins.
        /// </remarks>
        private static McpToolAuthorization ResolveAuthorization(ControllerActionDescriptor action)
        {
            var allowAnonymous = false;
            var authorizeData = new List<IAuthorizeData>();
            var policies = new List<AuthorizationPolicy>();

            CollectAuthorization(action.ControllerTypeInfo.GetCustomAttributes(inherit: true), ref allowAnonymous, authorizeData);
            CollectAuthorization(action.MethodInfo.GetCustomAttributes(inherit: true), ref allowAnonymous, authorizeData);

            if (action.FilterDescriptors != null)
            {
                foreach (var descriptor in action.FilterDescriptors)
                {
                    var filter = descriptor.Filter;
                    if (filter is IAllowAnonymousFilter)
                    {
                        allowAnonymous = true;
                        continue;
                    }

                    if (filter is AuthorizeFilter authorizeFilter)
                    {
                        if (authorizeFilter.AuthorizeData != null)
                        {
                            authorizeData.AddRange(authorizeFilter.AuthorizeData);
                        }

                        if (authorizeFilter.Policy != null)
                        {
                            policies.Add(authorizeFilter.Policy);
                        }

                        continue;
                    }

                    if (filter is IAuthorizeData filterData)
                    {
                        authorizeData.Add(filterData);
                    }
                }
            }

            return new McpToolAuthorization(allowAnonymous, authorizeData, policies);
        }

        private static void CollectAuthorization(object[] attributes, ref bool allowAnonymous, List<IAuthorizeData> authorizeData)
        {
            foreach (var attribute in attributes)
            {
                if (attribute is IAllowAnonymous)
                {
                    allowAnonymous = true;
                }
                else if (attribute is IAuthorizeData data)
                {
                    authorizeData.Add(data);
                }
            }
        }

        /// <summary>
        /// Narrows the action's full input list down to the set one <see cref="McpToolAttribute"/> asks for.
        /// Returns <c>false</c> when the variant cannot produce a callable tool.
        /// </summary>
        private bool TryApplyVariant(
            string display,
            McpToolAttribute? attribute,
            string routeTemplate,
            IReadOnlyList<McpToolParameterDescriptor> allParameters,
            out List<McpToolParameterDescriptor> parameters,
            out List<McpToolConstantDescriptor> constants)
        {
            parameters = new List<McpToolParameterDescriptor>(allParameters.Count);
            constants = new List<McpToolConstantDescriptor>();

            if (attribute == null)
            {
                parameters.AddRange(allParameters);
                return true;
            }

            var include = BuildNameSet(attribute.IncludeParameters);
            var exclude = BuildNameSet(attribute.ExcludeParameters);
            var required = BuildNameSet(attribute.RequiredParameters);
            var optional = BuildNameSet(attribute.OptionalParameters);
            var pinned = BuildConstantMap(attribute, display);

            WarnAboutUnknownNames(display, allParameters, include, exclude, required, optional, pinned?.Keys);

            foreach (var parameter in allParameters)
            {
                // The route template renders every token it still contains, so dropping one of those
                // inputs without pinning it would produce a tool that can never be invoked.
                var isRequiredRouteToken = parameter.Source == McpParameterSource.Route &&
                                           RouteTemplateHelper.ContainsToken(routeTemplate, parameter.BindingName);

                string? constantText;
                if (TryMatch(pinned, parameter, out constantText))
                {
                    var value = McpConstantValue.Convert(constantText!, parameter.ParameterType);
                    if (value == null && isRequiredRouteToken)
                    {
                        _logger.LogWarning(
                            "Nabu MCP skipped a [McpTool] variant on {Endpoint}: route parameter '{Parameter}' " +
                            "was pinned to an empty value but '{Template}' cannot be built without it.",
                            display,
                            parameter.Name,
                            routeTemplate);
                        return false;
                    }

                    constants.Add(new McpToolConstantDescriptor(
                        parameter.BindingName,
                        parameter.Source,
                        parameter.ParameterType,
                        value)
                    {
                        IsBodyRoot = parameter.IsBodyRoot,
                        ReplacedParameterName = parameter.Name,
                    });

                    continue;
                }

                var dropped = (include != null && !Matches(include, parameter)) ||
                              (exclude != null && Matches(exclude, parameter));

                if (dropped)
                {
                    if (isRequiredRouteToken)
                    {
                        _logger.LogWarning(
                            "Nabu MCP skipped a [McpTool] variant on {Endpoint}: route parameter '{Parameter}' " +
                            "was hidden but '{Template}' cannot be built without it. Pin it with ConstantParameters instead.",
                            display,
                            parameter.Name,
                            routeTemplate);
                        return false;
                    }

                    continue;
                }

                var isRequired = parameter.IsRequired;
                if (required != null && Matches(required, parameter))
                {
                    isRequired = true;
                }
                else if (optional != null && Matches(optional, parameter))
                {
                    isRequired = isRequiredRouteToken;
                }

                parameters.Add(isRequired == parameter.IsRequired ? parameter : WithRequired(parameter, isRequired));
            }

            return true;
        }

        private static McpToolParameterDescriptor WithRequired(McpToolParameterDescriptor parameter, bool isRequired)
        {
            return new McpToolParameterDescriptor(
                parameter.Name,
                parameter.BindingName,
                parameter.Source,
                parameter.ParameterType,
                isRequired,
                parameter.Schema)
            {
                IsBodyRoot = parameter.IsBodyRoot,
                Description = parameter.Description,
            };
        }

        private static ISet<string>? BuildNameSet(string[]? names)
        {
            if (names == null || names.Length == 0)
            {
                return null;
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    set.Add(name.Trim());
                }
            }

            return set.Count == 0 ? null : set;
        }

        private IDictionary<string, string>? BuildConstantMap(McpToolAttribute attribute, string display)
        {
            var entries = attribute.ConstantParameters;
            if (entries == null || entries.Length == 0)
            {
                return null;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                string name;
                string value;
                if (!McpConstantValue.TrySplit(entry, out name, out value))
                {
                    _logger.LogWarning(
                        "Nabu MCP ignored the ConstantParameters entry '{Entry}' on {Endpoint}: expected 'name=value'.",
                        entry,
                        display);
                    continue;
                }

                map[name] = value;
            }

            return map.Count == 0 ? null : map;
        }

        /// <summary>A parameter is addressable by its tool input name or by its underlying binding name.</summary>
        private static bool Matches(ISet<string> names, McpToolParameterDescriptor parameter)
        {
            return names.Contains(parameter.Name) || names.Contains(parameter.BindingName);
        }

        private static bool TryMatch(
            IDictionary<string, string>? map,
            McpToolParameterDescriptor parameter,
            out string? value)
        {
            value = null;
            if (map == null)
            {
                return false;
            }

            return map.TryGetValue(parameter.Name, out value) || map.TryGetValue(parameter.BindingName, out value);
        }

        /// <summary>
        /// A misspelled parameter name would otherwise fail silently - the variant would simply expose
        /// the full parameter set - so every configured name that matches nothing is reported.
        /// </summary>
        private void WarnAboutUnknownNames(
            string display,
            IReadOnlyList<McpToolParameterDescriptor> allParameters,
            params IEnumerable<string>?[] configuredNames)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in allParameters)
            {
                known.Add(parameter.Name);
                known.Add(parameter.BindingName);
            }

            foreach (var names in configuredNames)
            {
                if (names == null)
                {
                    continue;
                }

                foreach (var name in names)
                {
                    if (!known.Contains(name))
                    {
                        _logger.LogWarning(
                            "Nabu MCP ignored '{Name}' in an [McpTool] variant on {Endpoint}: the endpoint has no such input.",
                            name,
                            display);
                    }
                }
            }
        }

        private string ResolveName(
            ControllerActionDescriptor action,
            McpToolAttribute? methodAttribute,
            string httpMethod,
            string routeTemplate,
            ISet<string> usedNames)
        {
            var name = methodAttribute?.Name;
            if (string.IsNullOrEmpty(name))
            {
                var context = new McpToolNamingContext(action.ControllerName, action.ActionName, httpMethod, routeTemplate);
                name = _options.ToolNameFactory(context);
            }

            return ReserveName(name!, usedNames, action.ControllerName + "." + action.ActionName);
        }

        /// <summary>
        /// Sanitizes a tool name and claims it in <paramref name="usedNames"/>, appending a numeric
        /// suffix when overloads map to the same generated name.
        /// </summary>
        private string ReserveName(string name, ISet<string> usedNames, string display)
        {
            name = Sanitize(name);
            if (name.Length == 0)
            {
                name = "tool";
            }

            if (usedNames.Add(name))
            {
                return name;
            }

            var suffix = 2;
            string candidate;
            do
            {
                candidate = name + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                suffix++;
            }
            while (!usedNames.Add(candidate));

            _logger.LogWarning(
                "Nabu MCP tool name '{Name}' was already taken; {Endpoint} is exposed as '{Candidate}'.",
                name,
                display,
                candidate);

            return candidate;
        }

        internal static string Sanitize(string name)
        {
            var builder = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '_' || c == '-')
                {
                    builder.Append(c);
                }
                else if (builder.Length > 0 && builder[builder.Length - 1] != '_')
                {
                    builder.Append('_');
                }
            }

            return builder.ToString().Trim('_', '-');
        }

        private string? ResolveDescription(
            ControllerActionDescriptor action,
            McpToolAttribute? methodAttribute,
            McpToolAttribute? controllerAttribute,
            string httpMethod,
            string routeTemplate)
        {
            if (!string.IsNullOrEmpty(methodAttribute?.Description))
            {
                return methodAttribute!.Description;
            }

            var summary = _documentation.GetSummary(action.MethodInfo);
            if (!string.IsNullOrEmpty(summary))
            {
                var returns = _documentation.GetReturnsDescription(action.MethodInfo);
                return string.IsNullOrEmpty(returns) ? summary : summary + " Returns: " + returns;
            }

            if (!string.IsNullOrEmpty(controllerAttribute?.Description))
            {
                return controllerAttribute!.Description;
            }

            return "Invokes " + httpMethod + " /" + routeTemplate + " on the " + action.ControllerName + " API.";
        }

        private static McpToolAnnotations BuildAnnotations(
            McpToolAttribute? attribute,
            McpToolAttribute? methodAttribute,
            string httpMethod,
            string fallbackTitle)
        {
            var isRead = httpMethod == "GET" || httpMethod == "HEAD" || httpMethod == "OPTIONS";
            var annotations = new McpToolAnnotations
            {
                Title = methodAttribute?.Title ?? attribute?.Title ?? fallbackTitle,
                ReadOnly = isRead,
                Destructive = httpMethod == "DELETE" || httpMethod == "PUT",
                Idempotent = isRead || httpMethod == "PUT" || httpMethod == "DELETE",
                OpenWorld = false,
            };

            if (attribute != null)
            {
                if (attribute.ReadOnlyOverride.HasValue)
                {
                    annotations.ReadOnly = attribute.ReadOnlyOverride.Value;
                }

                if (attribute.DestructiveOverride.HasValue)
                {
                    annotations.Destructive = attribute.DestructiveOverride.Value;
                }

                if (attribute.IdempotentOverride.HasValue)
                {
                    annotations.Idempotent = attribute.IdempotentOverride.Value;
                }

                if (attribute.OpenWorldOverride.HasValue)
                {
                    annotations.OpenWorld = attribute.OpenWorldOverride.Value;
                }
            }

            return annotations;
        }

        internal static string Humanize(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                {
                    builder.Append(' ');
                }

                builder.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }

            return builder.ToString();
        }

        internal static string ResolveHttpMethod(ControllerActionDescriptor action)
        {
            var methods = new List<string>();

            // [HttpGet], [HttpPost], [AcceptVerbs(...)] and friends all implement this interface, and it
            // has lived in the same namespace since ASP.NET Core 1.0.
            foreach (var provider in action.MethodInfo.GetCustomAttributes(inherit: true).OfType<IActionHttpMethodProvider>())
            {
                if (provider.HttpMethods != null)
                {
                    methods.AddRange(provider.HttpMethods);
                }
            }

            if (methods.Count == 0 && action.ActionConstraints != null)
            {
                // HttpMethodActionConstraint moved namespaces between 2.2 and 3.0, so read it by shape.
                foreach (var constraint in action.ActionConstraints)
                {
                    var property = constraint.GetType().GetProperty("HttpMethods");
                    if (property != null && property.GetValue(constraint) is IEnumerable<string> values)
                    {
                        methods.AddRange(values);
                    }
                }
            }

            if (methods.Count > 0)
            {
                // Prefer a verb that is unambiguous for a tool call.
                foreach (var preferred in new[] { "POST", "PUT", "PATCH", "DELETE", "GET" })
                {
                    if (methods.Any(m => string.Equals(m, preferred, StringComparison.OrdinalIgnoreCase)))
                    {
                        return preferred;
                    }
                }

                return methods[0].ToUpperInvariant();
            }

            // No explicit verb: infer from whether anything is bound from the body or the form.
            var hasBody = action.Parameters.Any(p =>
                (p.BindingInfo?.BindingSource != null &&
                 (p.BindingInfo.BindingSource.Id == BindingSource.Body.Id ||
                  p.BindingInfo.BindingSource.Id == BindingSource.Form.Id ||
                  p.BindingInfo.BindingSource.Id == BindingSource.FormFile.Id)) ||
                GetFormParameterKind(p.ParameterType) != FormParameterKind.None);

            return hasBody ? "POST" : "GET";
        }

        private static string? ResolveRouteTemplate(ControllerActionDescriptor action)
        {
            var template = action.AttributeRouteInfo?.Template;
            if (!string.IsNullOrEmpty(template))
            {
                return RouteTemplateHelper.Normalize(template!);
            }

            // Conventional routing: reconstruct the default "{controller}/{action}" shape. Remaining
            // values bind from the query string, which the default model binder handles.
            string? controller;
            string? actionName;
            action.RouteValues.TryGetValue("controller", out controller);
            action.RouteValues.TryGetValue("action", out actionName);

            if (string.IsNullOrEmpty(controller) || string.IsNullOrEmpty(actionName))
            {
                return null;
            }

            string? area;
            action.RouteValues.TryGetValue("area", out area);

            return string.IsNullOrEmpty(area)
                ? controller + "/" + actionName
                : area + "/" + controller + "/" + actionName;
        }

        private bool TryBuildParameters(
            ControllerActionDescriptor action,
            string httpMethod,
            string routeTemplate,
            List<McpToolParameterDescriptor> parameters)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var parameter in action.Parameters)
            {
                var controllerParameter = parameter as ControllerParameterDescriptor;
                var parameterInfo = controllerParameter?.ParameterInfo;

                if (parameterInfo?.GetCustomAttribute<McpIgnoreAttribute>() != null)
                {
                    continue;
                }

                if (IsIgnoredType(parameter.ParameterType))
                {
                    continue;
                }

                var bindingName = parameter.BindingInfo?.BinderModelName ?? parameter.Name;
                var formKind = GetFormParameterKind(parameter.ParameterType);

                if (formKind == FormParameterKind.File || formKind == FormParameterKind.FileCollection)
                {
                    AddParameter(
                        action.MethodInfo,
                        parameter.Name,
                        bindingName,
                        parameter.ParameterType,
                        parameterInfo,
                        McpParameterSource.FormFile,
                        routeTemplate,
                        parameters,
                        seen,
                        schemaOverride: BuildFileArgumentSchema(formKind == FormParameterKind.FileCollection));
                    continue;
                }

                if (formKind == FormParameterKind.FormCollection)
                {
                    AddParameter(
                        action.MethodInfo,
                        parameter.Name,
                        bindingName,
                        parameter.ParameterType,
                        parameterInfo,
                        McpParameterSource.Form,
                        routeTemplate,
                        parameters,
                        seen,
                        schemaOverride: BuildFormCollectionSchema(),
                        isFormRoot: true);
                    continue;
                }

                var source = parameter.BindingInfo?.BindingSource;

                if (source != null)
                {
                    if (source.Id == BindingSource.Services.Id || source.Id == BindingSource.Special.Id)
                    {
                        continue;
                    }

                    if (source.Id == BindingSource.Header.Id && !_options.ExposeHeaderParameters)
                    {
                        continue;
                    }
                }

                var resolved = ResolveSource(source, parameter, parameterInfo, httpMethod, routeTemplate);
                AddParameter(
                    action.MethodInfo,
                    parameter.Name,
                    bindingName,
                    parameter.ParameterType,
                    parameterInfo,
                    resolved,
                    routeTemplate,
                    parameters,
                    seen);
            }

            return true;
        }

        private static McpParameterSource ResolveSource(
            BindingSource? source,
            Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor parameter,
            ParameterInfo? parameterInfo,
            string httpMethod,
            string routeTemplate)
        {
            if (source != null)
            {
                if (source.Id == BindingSource.Path.Id)
                {
                    return McpParameterSource.Route;
                }

                if (source.Id == BindingSource.Query.Id)
                {
                    return McpParameterSource.Query;
                }

                if (source.Id == BindingSource.Body.Id)
                {
                    return McpParameterSource.Body;
                }

                if (source.Id == BindingSource.Header.Id)
                {
                    return McpParameterSource.Header;
                }

                if (source.Id == BindingSource.Form.Id)
                {
                    return McpParameterSource.Form;
                }

                if (source.Id == BindingSource.FormFile.Id)
                {
                    return McpParameterSource.FormFile;
                }
            }

            var bindingName = parameter.BindingInfo?.BinderModelName ?? parameter.Name;
            if (RouteTemplateHelper.ContainsToken(routeTemplate, bindingName))
            {
                return McpParameterSource.Route;
            }

            var allowsBody = httpMethod == "POST" || httpMethod == "PUT" || httpMethod == "PATCH";
            if (allowsBody && JsonSchemaGenerator.IsComplexObject(parameter.ParameterType))
            {
                return McpParameterSource.Body;
            }

            return McpParameterSource.Query;
        }

        private void AddParameter(
            MethodBase? documentationMethod,
            string parameterName,
            string bindingName,
            Type type,
            ParameterInfo? parameterInfo,
            McpParameterSource source,
            string routeTemplate,
            List<McpToolParameterDescriptor> parameters,
            ISet<string> seen,
            bool valueTypesDefaultToOptional = true,
            PropertyInfo? propertyInfo = null,
            JsonObject? schemaOverride = null,
            bool isFormRoot = false)
        {
            var attributes = parameterInfo?.GetCustomAttributes().ToList() ?? new List<Attribute>();
            if (propertyInfo != null)
            {
                attributes.AddRange(propertyInfo.GetCustomAttributes());
            }

            var mcpAttribute = attributes.OfType<McpParameterAttribute>().FirstOrDefault();

            var description = JsonSchemaGenerator.ReadDescription(attributes)
                              ?? (documentationMethod != null
                                  ? _documentation.GetParameterDescription(documentationMethod, parameterName)
                                  : null)
                              ?? (propertyInfo != null ? _documentation.GetSummary(propertyInfo) : null);

            var isComplex = JsonSchemaGenerator.IsComplexObject(type);
            var shouldFlatten = schemaOverride == null &&
                                isComplex &&
                                (source == McpParameterSource.Query ||
                                 source == McpParameterSource.Form ||
                                 (source == McpParameterSource.Body && _options.FlattenBodyParameter));

            if (shouldFlatten)
            {
                IList<JsonSchemaGenerator.ExpandedProperty> properties;
                if (_schemaGenerator.TryExpandObject(type, out properties) && properties.Count > 0)
                {
                    foreach (var property in properties)
                    {
                        var name = Deduplicate(property.Name, seen);

                        // A file-typed property of a form model stays a file argument: it becomes a
                        // multipart part named after the property, which is where model binding looks.
                        var propertyKind = source == McpParameterSource.Form
                            ? GetFormParameterKind(property.ClrType)
                            : FormParameterKind.None;

                        if (propertyKind == FormParameterKind.File || propertyKind == FormParameterKind.FileCollection)
                        {
                            parameters.Add(new McpToolParameterDescriptor(
                                name,
                                property.Name,
                                McpParameterSource.FormFile,
                                property.ClrType,
                                property.IsRequired,
                                BuildFileArgumentSchema(propertyKind == FormParameterKind.FileCollection))
                            {
                                Description = property.Description,
                            });
                            continue;
                        }

                        parameters.Add(new McpToolParameterDescriptor(
                            name,
                            property.Name,
                            source,
                            property.ClrType,
                            property.IsRequired,
                            property.Schema)
                        {
                            Description = property.Description,
                        });
                    }

                    return;
                }
            }

            // Route and query arguments keep their public wire name, because that is the name the API
            // documents. A header name such as "X-Tenant" makes a poor tool argument, so header
            // parameters are advertised under their CLR parameter name instead.
            var schemaName = mcpAttribute?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(
                source == McpParameterSource.Header ? parameterName : bindingName);
            schemaName = Deduplicate(schemaName, seen);

            var nullable = parameterInfo != null
                ? NullabilityHelper.IsNullable(parameterInfo)
                : propertyInfo != null ? NullabilityHelper.IsNullable(propertyInfo) : null;
            var schema = schemaOverride ?? _schemaGenerator.Generate(type, attributes, nullable);

            if (!string.IsNullOrEmpty(description) && schema["description"] == null)
            {
                schema["description"] = description;
            }

            var isRequired = ResolveRequired(
                type,
                parameterInfo?.HasDefaultValue == true,
                attributes,
                mcpAttribute,
                source,
                routeTemplate,
                bindingName,
                nullable,
                valueTypesDefaultToOptional);

            parameters.Add(new McpToolParameterDescriptor(schemaName, bindingName, source, type, isRequired, schema)
            {
                IsBodyRoot = source == McpParameterSource.Body || isFormRoot,
                Description = description,
            });
        }

        private static string Deduplicate(string name, ISet<string> seen)
        {
            if (seen.Add(name))
            {
                return name;
            }

            var suffix = 2;
            string candidate;
            do
            {
                candidate = name + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                suffix++;
            }
            while (!seen.Add(candidate));

            return candidate;
        }

        /// <remarks>
        /// <paramref name="valueTypesDefaultToOptional"/> encodes a real difference between the two
        /// binding stacks: MVC model binding gives a missing non-nullable value type its default value,
        /// while Minimal APIs answer 400 - so the same <c>int page</c> parameter is optional on a
        /// controller and required on a route handler.
        /// </remarks>
        private static bool ResolveRequired(
            Type parameterType,
            bool hasDefaultValue,
            IList<Attribute> attributes,
            McpParameterAttribute? mcpAttribute,
            McpParameterSource source,
            string routeTemplate,
            string bindingName,
            bool? nullable,
            bool valueTypesDefaultToOptional)
        {
            if (mcpAttribute?.RequiredOverride != null)
            {
                return mcpAttribute.RequiredOverride.Value;
            }

            if (attributes.Any(a => a is RequiredAttribute) || attributes.Any(a => a is BindRequiredAttribute))
            {
                return true;
            }

            if (hasDefaultValue)
            {
                return false;
            }

            if (source == McpParameterSource.Route)
            {
                return RouteTemplateHelper.ContainsToken(routeTemplate, bindingName);
            }

            if (source == McpParameterSource.Body || source == McpParameterSource.FormFile)
            {
                return nullable != true;
            }

            if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
            {
                return !valueTypesDefaultToOptional;
            }

            return nullable == false;
        }

        private static JsonObject BuildInputSchema(IReadOnlyList<McpToolParameterDescriptor> parameters)
        {
            var properties = new JsonObject();
            var required = new JsonArray();

            foreach (var parameter in parameters)
            {
                properties[parameter.Name] = JsonHelpers.Clone(parameter.Schema);
                if (parameter.IsRequired)
                {
                    required.Add(parameter.Name);
                }
            }

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
            };

            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            return schema;
        }

        private static bool IsIgnoredType(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            foreach (var ignored in IgnoredParameterTypes)
            {
                if (ignored.IsAssignableFrom(underlying))
                {
                    return true;
                }
            }

            return false;
        }

        internal static FormParameterKind GetFormParameterKind(Type type)
        {
            if (typeof(IFormFile).IsAssignableFrom(type))
            {
                return FormParameterKind.File;
            }

            if (typeof(IFormFileCollection).IsAssignableFrom(type))
            {
                return FormParameterKind.FileCollection;
            }

            var element = JsonSchemaGenerator.GetEnumerableElementType(type);
            if (element != null && typeof(IFormFile).IsAssignableFrom(element))
            {
                return FormParameterKind.FileCollection;
            }

            if (typeof(IFormCollection).IsAssignableFrom(type))
            {
                return FormParameterKind.FormCollection;
            }

            return FormParameterKind.None;
        }

        /// <summary>
        /// The schema advertised for a file argument. The caller supplies the content base64-encoded;
        /// the binder turns it into a part of a multipart/form-data body, which the action's own model
        /// binding reads back as an <see cref="IFormFile"/>.
        /// </summary>
        internal static JsonObject BuildFileSchema()
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["data"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["contentEncoding"] = "base64",
                        ["description"] = "The file content, base64-encoded.",
                    },
                    ["fileName"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "The file name reported to the endpoint.",
                    },
                    ["contentType"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "The MIME type of the content. Defaults to application/octet-stream.",
                    },
                },
                ["required"] = new JsonArray("data"),
            };
        }

        /// <summary>The file schema, or an array of it for a collection-of-files parameter.</summary>
        internal static JsonObject BuildFileArgumentSchema(bool isCollection)
        {
            var schema = BuildFileSchema();
            return isCollection
                ? new JsonObject { ["type"] = "array", ["items"] = schema }
                : schema;
        }

        /// <summary>The schema advertised for a whole-form argument (<see cref="IFormCollection"/>).</summary>
        internal static JsonObject BuildFormCollectionSchema()
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["description"] = "Form fields as name/value pairs. Array values repeat the field.",
                ["additionalProperties"] = new JsonObject(),
            };
        }
    }
}
