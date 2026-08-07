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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    /// Discovers MCP tools from the MVC action table. The result is cached and rebuilt automatically
    /// whenever the action descriptor collection changes (for example when application parts are added).
    /// </summary>
    public class McpToolRegistry : IMcpToolRegistry
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

        private static readonly string[] FormParameterTypeNames =
        {
            "Microsoft.AspNetCore.Http.IFormFile",
            "Microsoft.AspNetCore.Http.IFormFileCollection",
            "Microsoft.AspNetCore.Http.IFormCollection",
        };

        private readonly IActionDescriptorCollectionProvider _actionProvider;
        private readonly NabuMcpOptions _options;
        private readonly JsonSchemaGenerator _schemaGenerator;
        private readonly IXmlDocumentationProvider _documentation;
        private readonly ILogger _logger;

        private readonly object _sync = new object();
        private int _cachedVersion = -1;
        private IReadOnlyList<McpToolDescriptor>? _tools;
        private IReadOnlyDictionary<string, McpToolDescriptor>? _byName;

        public McpToolRegistry(
            IActionDescriptorCollectionProvider actionProvider,
            IOptions<NabuMcpOptions> options,
            JsonSchemaGenerator schemaGenerator,
            IXmlDocumentationProvider documentation,
            ILogger<McpToolRegistry>? logger = null)
        {
            _actionProvider = actionProvider ?? throw new ArgumentNullException(nameof(actionProvider));
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _schemaGenerator = schemaGenerator ?? throw new ArgumentNullException(nameof(schemaGenerator));
            _documentation = documentation ?? NullXmlDocumentationProvider.Instance;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
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

        private void EnsureBuilt()
        {
            var collection = _actionProvider.ActionDescriptors;
            if (_tools != null && _cachedVersion == collection.Version)
            {
                return;
            }

            lock (_sync)
            {
                collection = _actionProvider.ActionDescriptors;
                if (_tools != null && _cachedVersion == collection.Version)
                {
                    return;
                }

                var tools = Build(collection.Items);
                var byName = new Dictionary<string, McpToolDescriptor>(StringComparer.Ordinal);
                foreach (var tool in tools)
                {
                    byName[tool.Name] = tool;
                }

                _tools = tools;
                _byName = byName;
                _cachedVersion = collection.Version;

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

                McpToolDescriptor? tool;
                try
                {
                    tool = TryCreateTool(action, used);
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

                if (tool == null)
                {
                    continue;
                }

                if (_options.ToolFilter != null && !_options.ToolFilter(tool))
                {
                    continue;
                }

                results.Add(tool);
            }

            results.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return results;
        }

        private McpToolDescriptor? TryCreateTool(ControllerActionDescriptor action, ISet<string> usedNames)
        {
            var method = action.MethodInfo;
            var controllerType = action.ControllerTypeInfo;

            if (method.GetCustomAttribute<McpIgnoreAttribute>() != null ||
                controllerType.GetCustomAttribute<McpIgnoreAttribute>() != null)
            {
                return null;
            }

            var methodAttribute = method.GetCustomAttribute<McpToolAttribute>(inherit: true);
            var controllerAttribute = controllerType.GetCustomAttribute<McpToolAttribute>(inherit: true);
            var attribute = methodAttribute ?? controllerAttribute;

            if (attribute == null && !_options.ExposeAllActions)
            {
                return null;
            }

            if (attribute != null && !attribute.Enabled)
            {
                return null;
            }

            if (attribute == null)
            {
                // Blanket exposure still respects the API explorer opt-out.
                var apiExplorer = method.GetCustomAttribute<ApiExplorerSettingsAttribute>()
                                  ?? controllerType.GetCustomAttribute<ApiExplorerSettingsAttribute>();
                if (apiExplorer != null && apiExplorer.IgnoreApi)
                {
                    return null;
                }
            }

            var httpMethod = ResolveHttpMethod(action);
            var routeTemplate = ResolveRouteTemplate(action);
            if (routeTemplate == null)
            {
                _logger.LogWarning(
                    "Nabu MCP skipped {Controller}.{Action}: no route template could be resolved. Attribute routing is required.",
                    action.ControllerName,
                    action.ActionName);
                return null;
            }

            var parameters = new List<McpToolParameterDescriptor>();
            if (!TryBuildParameters(action, httpMethod, routeTemplate, parameters))
            {
                return null;
            }

            var inputSchema = BuildInputSchema(parameters);
            var annotations = BuildAnnotations(attribute, methodAttribute, httpMethod, action);
            var name = ResolveName(action, methodAttribute, httpMethod, routeTemplate, usedNames);

            var tool = new McpToolDescriptor(name, httpMethod, routeTemplate, action, parameters, inputSchema, annotations)
            {
                Description = ResolveDescription(action, methodAttribute, controllerAttribute, httpMethod, routeTemplate),
                ConstantRouteValues = new Dictionary<string, string?>(action.RouteValues, StringComparer.OrdinalIgnoreCase),
            };

            return tool;
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

            name = Sanitize(name!);
            if (name.Length == 0)
            {
                name = "tool";
            }

            if (usedNames.Add(name))
            {
                return name;
            }

            // Overloaded actions map to the same generated name; disambiguate deterministically.
            var suffix = 2;
            string candidate;
            do
            {
                candidate = name + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
                suffix++;
            }
            while (!usedNames.Add(candidate));

            _logger.LogWarning(
                "Nabu MCP tool name '{Name}' was already taken; {Controller}.{Action} is exposed as '{Candidate}'.",
                name,
                action.ControllerName,
                action.ActionName,
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
            ControllerActionDescriptor action)
        {
            var isRead = httpMethod == "GET" || httpMethod == "HEAD" || httpMethod == "OPTIONS";
            var annotations = new McpToolAnnotations
            {
                Title = methodAttribute?.Title ?? attribute?.Title ?? Humanize(action.ActionName),
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

            // No explicit verb: infer from whether anything is bound from the body.
            var hasBody = action.Parameters.Any(p =>
                p.BindingInfo?.BindingSource != null && p.BindingInfo.BindingSource.Id == BindingSource.Body.Id);

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

                if (IsFormType(parameter.ParameterType))
                {
                    _logger.LogWarning(
                        "Nabu MCP skipped {Controller}.{Action}: parameter '{Parameter}' binds form data, which tools cannot supply.",
                        action.ControllerName,
                        action.ActionName,
                        parameter.Name);
                    return false;
                }

                var source = parameter.BindingInfo?.BindingSource;

                if (source != null)
                {
                    if (source.Id == BindingSource.Services.Id || source.Id == BindingSource.Special.Id)
                    {
                        continue;
                    }

                    if (source.Id == BindingSource.Form.Id || source.Id == BindingSource.FormFile.Id)
                    {
                        _logger.LogWarning(
                            "Nabu MCP skipped {Controller}.{Action}: parameter '{Parameter}' binds form data.",
                            action.ControllerName,
                            action.ActionName,
                            parameter.Name);
                        return false;
                    }

                    if (source.Id == BindingSource.Header.Id && !_options.ExposeHeaderParameters)
                    {
                        continue;
                    }
                }

                var resolved = ResolveSource(source, parameter, parameterInfo, httpMethod, routeTemplate);
                AddParameter(action, parameter, parameterInfo, resolved, routeTemplate, parameters, seen);
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
            ControllerActionDescriptor action,
            Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor parameter,
            ParameterInfo? parameterInfo,
            McpParameterSource source,
            string routeTemplate,
            List<McpToolParameterDescriptor> parameters,
            ISet<string> seen)
        {
            var bindingName = parameter.BindingInfo?.BinderModelName ?? parameter.Name;
            var attributes = parameterInfo?.GetCustomAttributes().ToList() ?? new List<Attribute>();
            var mcpAttribute = parameterInfo?.GetCustomAttribute<McpParameterAttribute>();
            var type = parameter.ParameterType;

            var description = JsonSchemaGenerator.ReadDescription(attributes)
                              ?? _documentation.GetParameterDescription(action.MethodInfo, parameter.Name);

            var isComplex = JsonSchemaGenerator.IsComplexObject(type);
            var shouldFlatten = isComplex &&
                                (source == McpParameterSource.Query ||
                                 (source == McpParameterSource.Body && _options.FlattenBodyParameter));

            if (shouldFlatten)
            {
                IList<JsonSchemaGenerator.ExpandedProperty> properties;
                if (_schemaGenerator.TryExpandObject(type, out properties) && properties.Count > 0)
                {
                    foreach (var property in properties)
                    {
                        var name = Deduplicate(property.Name, seen);
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
                source == McpParameterSource.Header ? parameter.Name : bindingName);
            schemaName = Deduplicate(schemaName, seen);

            var nullable = parameterInfo != null ? NullabilityHelper.IsNullable(parameterInfo) : null;
            var schema = _schemaGenerator.Generate(type, attributes, nullable);

            if (!string.IsNullOrEmpty(description) && schema["description"] == null)
            {
                schema["description"] = description;
            }

            var isRequired = ResolveRequired(parameter, parameterInfo, attributes, mcpAttribute, source, routeTemplate, bindingName, nullable);

            parameters.Add(new McpToolParameterDescriptor(schemaName, bindingName, source, type, isRequired, schema)
            {
                IsBodyRoot = source == McpParameterSource.Body,
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

        private static bool ResolveRequired(
            Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor parameter,
            ParameterInfo? parameterInfo,
            IList<Attribute> attributes,
            McpParameterAttribute? mcpAttribute,
            McpParameterSource source,
            string routeTemplate,
            string bindingName,
            bool? nullable)
        {
            if (mcpAttribute?.RequiredOverride != null)
            {
                return mcpAttribute.RequiredOverride.Value;
            }

            if (attributes.Any(a => a is RequiredAttribute) || attributes.Any(a => a is BindRequiredAttribute))
            {
                return true;
            }

            if (parameterInfo != null && parameterInfo.HasDefaultValue)
            {
                return false;
            }

            if (source == McpParameterSource.Route)
            {
                return RouteTemplateHelper.ContainsToken(routeTemplate, bindingName);
            }

            if (source == McpParameterSource.Body)
            {
                return nullable != true;
            }

            var type = parameter.ParameterType;
            if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
            {
                return false;
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

        private static bool IsFormType(Type type)
        {
            var candidates = new List<Type> { type };
            var element = JsonSchemaGenerator.GetEnumerableElementType(type);
            if (element != null)
            {
                candidates.Add(element);
            }

            foreach (var candidate in candidates)
            {
                var name = candidate.FullName;
                if (name != null && Array.IndexOf(FormParameterTypeNames, name) >= 0)
                {
                    return true;
                }

                foreach (var @interface in candidate.GetInterfaces())
                {
                    if (@interface.FullName != null && Array.IndexOf(FormParameterTypeNames, @interface.FullName) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
