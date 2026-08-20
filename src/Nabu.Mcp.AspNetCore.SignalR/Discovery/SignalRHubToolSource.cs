using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Schema;
using Nabu.Mcp.AspNetCore.SignalR.Execution;

namespace Nabu.Mcp.AspNetCore.SignalR.Discovery
{
    /// <summary>
    /// Discovers MCP tools from the SignalR hubs the application has mapped. Hubs are found through
    /// the endpoint table (<c>MapHub&lt;THub&gt;</c> stamps <see cref="HubMetadata"/> onto its
    /// endpoints), methods are published with the same <c>[McpTool]</c> / <c>[McpIgnore]</c> /
    /// <c>[McpParameter]</c> attributes controllers use, and every published tool dispatches to
    /// <see cref="SignalRHubToolInvoker"/> instead of the HTTP pipeline replay.
    /// </summary>
    public sealed class SignalRHubToolSource : IMcpToolSource
    {
        /// <summary>
        /// Ties invocation metadata to descriptors without widening the descriptor type. Entries die
        /// with their descriptors, so registry rebuilds do not leak.
        /// </summary>
        private static readonly ConditionalWeakTable<McpToolDescriptor, SignalRHubToolMetadata> MetadataTable =
            new ConditionalWeakTable<McpToolDescriptor, SignalRHubToolMetadata>();

        private static readonly string[] LifecycleMethods = { nameof(Hub.OnConnectedAsync), nameof(Hub.OnDisconnectedAsync), nameof(Hub.Dispose) };

        private readonly EndpointDataSource? _endpointDataSource;
        private readonly NabuMcpOptions _coreOptions;
        private readonly NabuMcpSignalROptions _options;
        private readonly JsonSchemaGenerator _schemaGenerator;
        private readonly IXmlDocumentationProvider _documentation;
        private readonly ILogger _logger;

        public SignalRHubToolSource(
            EndpointDataSource? endpointDataSource,
            IOptions<NabuMcpOptions> coreOptions,
            IOptions<NabuMcpSignalROptions> options,
            JsonSchemaGenerator schemaGenerator,
            IXmlDocumentationProvider documentation,
            ILogger<SignalRHubToolSource>? logger = null)
        {
            _endpointDataSource = endpointDataSource;
            _coreOptions = (coreOptions ?? throw new ArgumentNullException(nameof(coreOptions))).Value;
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _schemaGenerator = schemaGenerator ?? throw new ArgumentNullException(nameof(schemaGenerator));
            _documentation = documentation ?? NullXmlDocumentationProvider.Instance;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        internal static bool TryGetMetadata(McpToolDescriptor tool, out SignalRHubToolMetadata? metadata)
        {
            return MetadataTable.TryGetValue(tool, out metadata);
        }

        public IReadOnlyList<McpToolDescriptor> GetTools()
        {
            var results = new List<McpToolDescriptor>();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var hub in FindMappedHubs())
            {
                try
                {
                    results.AddRange(CreateHubTools(hub.HubType, hub.Route, usedNames));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Nabu MCP could not expose hub {Hub} as tools.", hub.HubType.FullName);
                }
            }

            return results;
        }

        private IEnumerable<(Type HubType, string Route)> FindMappedHubs()
        {
            if (_endpointDataSource == null)
            {
                yield break;
            }

            var seen = new HashSet<Type>();
            foreach (var endpoint in _endpointDataSource.Endpoints)
            {
                var hubMetadata = endpoint.Metadata.GetMetadata<HubMetadata>();
                if (hubMetadata == null || !seen.Add(hubMetadata.HubType))
                {
                    continue;
                }

                var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? hubMetadata.HubType.Name;

                // MapHub registers "{path}" and "{path}/negotiate"; either may come first. Normalize
                // to the hub path so tool diagnostics read naturally.
                const string negotiateSuffix = "/negotiate";
                if (route.EndsWith(negotiateSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    route = route.Substring(0, route.Length - negotiateSuffix.Length);
                }

                yield return (hubMetadata.HubType, route);
            }
        }

        /// <summary>Builds every tool one hub contributes. Exposed for tests.</summary>
        internal IReadOnlyList<McpToolDescriptor> CreateHubTools(Type hubType, string route, ISet<string> usedNames)
        {
            var results = new List<McpToolDescriptor>();

            if (hubType.GetCustomAttribute<McpIgnoreAttribute>(inherit: true) != null)
            {
                return results;
            }

            var classAttributes = hubType.GetCustomAttributes<McpToolAttribute>(inherit: true).ToList();
            var classAuthorizeData = hubType.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
            var classAllowsAnonymous = hubType.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();
            var hubName = TrimHubSuffix(hubType.Name);

            foreach (var method in hubType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!IsCandidate(method))
                {
                    continue;
                }

                if (method.GetCustomAttribute<McpIgnoreAttribute>(inherit: true) != null)
                {
                    continue;
                }

                var methodAttributes = method.GetCustomAttributes<McpToolAttribute>(inherit: true).ToList();

                // Same semantics as controllers: a hub-wide [McpTool] is a default for methods that
                // carry none of their own; a method that declares variants replaces it.
                var variants = new List<McpToolAttribute?>();
                if (methodAttributes.Count > 0)
                {
                    variants.AddRange(methodAttributes);
                }
                else if (classAttributes.Count > 0)
                {
                    variants.Add(classAttributes[0]);
                }
                else if (_options.ExposeAllHubMethods)
                {
                    variants.Add(null);
                }
                else
                {
                    continue;
                }

                var parameters = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).ToList();
                if (parameters.Any(p => IsClientStream(p.ParameterType)))
                {
                    _logger.LogWarning(
                        "Nabu MCP skipped hub method {Hub}.{Method}: client-to-server streaming parameters are not supported.",
                        hubType.Name,
                        method.Name);
                    continue;
                }

                foreach (var variant in variants)
                {
                    if (variant != null && !variant.Enabled)
                    {
                        continue;
                    }

                    // Class-level [McpTool] names/param shaping apply to a single action for
                    // controllers; on a hub, a class-level Name would collide across methods, so it
                    // is ignored the same way the controller path ignores it per action.
                    var useVariantIdentity = variant != null && methodAttributes.Count > 0;

                    var tool = CreateTool(
                        hubType, hubName, route, method, parameters,
                        useVariantIdentity ? variant : null,
                        classAuthorizeData, classAllowsAnonymous, usedNames);
                    if (tool != null)
                    {
                        results.Add(tool);
                    }
                }
            }

            return results;
        }

        private McpToolDescriptor? CreateTool(
            Type hubType,
            string hubName,
            string route,
            MethodInfo method,
            IReadOnlyList<ParameterInfo> parameters,
            McpToolAttribute? attribute,
            IReadOnlyList<IAuthorizeData> classAuthorizeData,
            bool classAllowsAnonymous,
            ISet<string> usedNames)
        {
            var display = hubType.Name + "." + method.Name;

            // Constants first: a pinned parameter disappears from the schema.
            var constants = new List<McpToolConstantDescriptor>();
            var constantNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in attribute?.ConstantParameters ?? Array.Empty<string>())
            {
                if (!McpConstantValue.TrySplit(entry, out var constantName, out var rawValue))
                {
                    _logger.LogWarning("Nabu MCP ignored the ConstantParameters entry '{Entry}' on {Method}: expected 'name=value'.", entry, display);
                    continue;
                }

                var target = parameters.FirstOrDefault(p => NamesMatch(p, constantName));
                if (target == null)
                {
                    _logger.LogWarning("Nabu MCP ignored the constant '{Name}' on {Method}: no parameter has that name.", constantName, display);
                    continue;
                }

                constantNames.Add(target.Name!);
                constants.Add(new McpToolConstantDescriptor(target.Name!, McpParameterSource.Body, target.ParameterType, McpConstantValue.Convert(rawValue, target.ParameterType))
                {
                    ReplacedParameterName = SchemaName(target),
                });
            }

            var include = ToNameSet(attribute?.IncludeParameters);
            var exclude = ToNameSet(attribute?.ExcludeParameters);
            var required = ToNameSet(attribute?.RequiredParameters);
            var optional = ToNameSet(attribute?.OptionalParameters);

            var toolParameters = new List<McpToolParameterDescriptor>();
            foreach (var parameter in parameters)
            {
                if (constantNames.Contains(parameter.Name!))
                {
                    continue;
                }

                var hidden = (include != null && !Matches(include, parameter)) || (exclude != null && Matches(exclude, parameter));
                if (hidden)
                {
                    if (!parameter.HasDefaultValue && !IsNullableParameter(parameter))
                    {
                        _logger.LogWarning(
                            "Nabu MCP skipped the tool variant on {Method}: parameter '{Name}' was hidden but the method cannot be called without it. Pin it with ConstantParameters instead.",
                            display,
                            parameter.Name);
                        return null;
                    }

                    continue;
                }

                toolParameters.Add(CreateParameter(method, parameter, required, optional));
            }

            var name = attribute?.Name;
            if (string.IsNullOrEmpty(name))
            {
                name = _coreOptions.ToolNameFactory(new McpToolNamingContext(hubName, method.Name, "HUB", route.TrimStart('/') + "/" + method.Name));
            }

            name = ReserveName(McpToolRegistry.Sanitize(name!), usedNames, display);

            var annotations = new McpToolAnnotations
            {
                Title = attribute?.Title ?? McpToolRegistry.Humanize(method.Name),
                // Unlike HTTP, a hub method's verb says nothing about its behaviour; assume the
                // conservative defaults and let the attribute override them.
                ReadOnly = attribute?.ReadOnlyOverride ?? false,
                Destructive = attribute?.DestructiveOverride ?? false,
                Idempotent = attribute?.IdempotentOverride ?? false,
                OpenWorld = attribute?.OpenWorldOverride ?? false,
            };

            var descriptor = new McpToolDescriptor(name, toolParameters, McpToolRegistry.BuildInputSchema(toolParameters), annotations)
            {
                Description = attribute?.Description ?? _documentation.GetSummary(method) ?? "SignalR hub method " + display + ".",
                InvokerType = typeof(SignalRHubToolInvoker),
                Constants = constants,
                Authorization = BuildAuthorization(method, classAuthorizeData, classAllowsAnonymous),
            };

            MetadataTable.Add(descriptor, new SignalRHubToolMetadata(
                hubType,
                method,
                IsStreamingReturn(method.ReturnType),
                parameters,
                classAuthorizeData,
                classAllowsAnonymous));

            return descriptor;
        }

        private McpToolParameterDescriptor CreateParameter(
            MethodInfo method,
            ParameterInfo parameter,
            HashSet<string>? required,
            HashSet<string>? optional)
        {
            var attribute = parameter.GetCustomAttribute<McpParameterAttribute>();
            var schemaName = attribute?.Name ?? parameter.Name!;
            var nullable = IsNullableParameter(parameter);

            var isRequired = attribute?.RequiredOverride ?? (!parameter.HasDefaultValue && !nullable);
            if (required != null && (required.Contains(schemaName) || required.Contains(parameter.Name!)))
            {
                isRequired = true;
            }
            else if (optional != null && (optional.Contains(schemaName) || optional.Contains(parameter.Name!)))
            {
                // A parameter the method cannot be called without stays required regardless.
                isRequired = !parameter.HasDefaultValue && !nullable;
            }

            var schema = _schemaGenerator.Generate(parameter.ParameterType, parameter.GetCustomAttributes(), nullable ? true : (bool?)null);

            var description = attribute?.Description ?? _documentation.GetParameterDescription(method, parameter.Name!);
            if (!string.IsNullOrEmpty(description))
            {
                schema["description"] = description;
            }

            if (attribute?.Example != null)
            {
                schema["examples"] = new System.Text.Json.Nodes.JsonArray(System.Text.Json.Nodes.JsonValue.Create(attribute.Example.ToString()));
            }

            return new McpToolParameterDescriptor(schemaName, parameter.Name!, McpParameterSource.Body, parameter.ParameterType, isRequired, schema)
            {
                Description = description,
            };
        }

        private static McpToolAuthorization BuildAuthorization(
            MethodInfo method,
            IReadOnlyList<IAuthorizeData> classAuthorizeData,
            bool classAllowsAnonymous)
        {
            var methodAuthorizeData = method.GetCustomAttributes(inherit: true).OfType<IAuthorizeData>().ToList();
            var methodAllowsAnonymous = method.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any();

            var allData = new List<IAuthorizeData>(classAuthorizeData.Count + methodAuthorizeData.Count);
            if (!classAllowsAnonymous)
            {
                allData.AddRange(classAuthorizeData);
            }

            if (!methodAllowsAnonymous)
            {
                allData.AddRange(methodAuthorizeData);
            }

            // The class requirement gates the connection itself, so - unlike MVC - a method-level
            // [AllowAnonymous] cannot opt out of it: a caller that cannot connect calls nothing.
            var connectionRequiresAuthorization = classAuthorizeData.Count > 0 && !classAllowsAnonymous;
            var methodRequiresAuthorization = methodAuthorizeData.Count > 0 && !methodAllowsAnonymous;

            var allowsAnonymous = !connectionRequiresAuthorization
                && !methodRequiresAuthorization
                && (classAllowsAnonymous || methodAllowsAnonymous);

            return new McpToolAuthorization(allowsAnonymous, allData, null);
        }

        private static bool IsCandidate(MethodInfo method)
        {
            if (method.IsSpecialName || method.IsGenericMethod || method.DeclaringType == null)
            {
                return false;
            }

            // Everything the base Hub types and object provide is infrastructure, and so are the
            // lifecycle methods even when the application hub overrides them.
            if (method.DeclaringType == typeof(object) || IsHubBaseType(method.DeclaringType))
            {
                return false;
            }

            return !LifecycleMethods.Contains(method.Name, StringComparer.Ordinal);
        }

        private static bool IsHubBaseType(Type type)
        {
            return type == typeof(Hub) || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Hub<>));
        }

        private static bool IsClientStream(Type type)
        {
            if (!type.IsGenericType)
            {
                return false;
            }

            var definition = type.GetGenericTypeDefinition();
            return definition == typeof(ChannelReader<>) || definition == typeof(IAsyncEnumerable<>);
        }

        internal static bool IsStreamingReturn(Type returnType)
        {
            var unwrapped = returnType;
            if (unwrapped.IsGenericType)
            {
                var definition = unwrapped.GetGenericTypeDefinition();
                if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
                {
                    unwrapped = unwrapped.GetGenericArguments()[0];
                }
            }

            return IsClientStream(unwrapped);
        }

        private static bool IsNullableParameter(ParameterInfo parameter)
        {
            return NullabilityHelper.IsNullable(parameter) == true;
        }

        private static string TrimHubSuffix(string name)
        {
            return name.Length > 3 && name.EndsWith("Hub", StringComparison.Ordinal)
                ? name.Substring(0, name.Length - 3)
                : name;
        }

        private static HashSet<string>? ToNameSet(string[]? names)
        {
            return names == null || names.Length == 0
                ? null
                : new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }

        private static bool Matches(HashSet<string> names, ParameterInfo parameter)
        {
            var attribute = parameter.GetCustomAttribute<McpParameterAttribute>();
            return names.Contains(parameter.Name!) || (attribute?.Name != null && names.Contains(attribute.Name));
        }

        private static bool NamesMatch(ParameterInfo parameter, string name)
        {
            return string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(SchemaName(parameter), name, StringComparison.OrdinalIgnoreCase);
        }

        private static string SchemaName(ParameterInfo parameter)
        {
            return parameter.GetCustomAttribute<McpParameterAttribute>()?.Name ?? parameter.Name!;
        }

        private string ReserveName(string name, ISet<string> usedNames, string display)
        {
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
                "Nabu MCP generated the duplicate tool name '{Name}' for {Method}; it was published as '{Candidate}'. Give it an explicit [McpTool(Name = ...)].",
                name,
                display,
                candidate);
            return candidate;
        }
    }
}
