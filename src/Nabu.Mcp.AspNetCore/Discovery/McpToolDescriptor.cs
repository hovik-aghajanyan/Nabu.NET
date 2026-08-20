using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>Where a tool argument ends up on the synthetic HTTP request.</summary>
    public enum McpParameterSource
    {
        /// <summary>Substituted into a route template token.</summary>
        Route,

        /// <summary>Appended to the query string.</summary>
        Query,

        /// <summary>Written into the JSON request body.</summary>
        Body,

        /// <summary>Sent as a request header.</summary>
        Header,

        /// <summary>Sent as a form field in the request body.</summary>
        Form,

        /// <summary>Sent as a file part of a multipart/form-data request body.</summary>
        FormFile,
    }

    /// <summary>A single input of a tool, and how it maps back onto the action's binding sources.</summary>
    public sealed class McpToolParameterDescriptor
    {
        public McpToolParameterDescriptor(
            string name,
            string bindingName,
            McpParameterSource source,
            Type parameterType,
            bool isRequired,
            JsonObject schema)
        {
            Name = name;
            BindingName = bindingName;
            Source = source;
            ParameterType = parameterType;
            IsRequired = isRequired;
            Schema = schema;
        }

        /// <summary>Property name in the tool's input schema.</summary>
        public string Name { get; }

        /// <summary>
        /// Name used when writing the value onto the request: the route token, query key, header name,
        /// or JSON property inside the request body.
        /// </summary>
        public string BindingName { get; }

        public McpParameterSource Source { get; }

        public Type ParameterType { get; }

        public bool IsRequired { get; }

        public JsonObject Schema { get; }

        /// <summary>
        /// True when this argument *is* the entire request body rather than one property of it.
        /// </summary>
        public bool IsBodyRoot { get; set; }

        public string? Description { get; set; }
    }

    /// <summary>
    /// A value pinned by the tool rather than supplied by the caller. Constants are invisible to the
    /// model - they carry no schema - but are always written onto the underlying HTTP request.
    /// </summary>
    public sealed class McpToolConstantDescriptor
    {
        public McpToolConstantDescriptor(
            string bindingName,
            McpParameterSource source,
            Type parameterType,
            JsonNode? value)
        {
            BindingName = bindingName;
            Source = source;
            ParameterType = parameterType;
            Value = value;
        }

        /// <summary>Route token, query key, header name, or JSON property inside the request body.</summary>
        public string BindingName { get; }

        public McpParameterSource Source { get; }

        public Type ParameterType { get; }

        /// <summary>The pinned value, already converted to the shape the action expects.</summary>
        public JsonNode? Value { get; }

        /// <summary>True when this value <em>is</em> the whole request body.</summary>
        public bool IsBodyRoot { get; set; }

        /// <summary>The tool input name this constant replaced, kept for diagnostics.</summary>
        public string? ReplacedParameterName { get; set; }
    }

    /// <summary>MCP tool annotations, mapped from HTTP semantics unless overridden.</summary>
    public sealed class McpToolAnnotations
    {
        public string? Title { get; set; }

        public bool ReadOnly { get; set; }

        public bool Destructive { get; set; }

        public bool Idempotent { get; set; }

        public bool OpenWorld { get; set; }
    }

    /// <summary>
    /// Everything Nabu needs to advertise and invoke one HTTP endpoint - a controller action or a
    /// Minimal API route handler - as an MCP tool.
    /// </summary>
    public sealed class McpToolDescriptor
    {
        public McpToolDescriptor(
            string name,
            string httpMethod,
            string routeTemplate,
            ControllerActionDescriptor action,
            IReadOnlyList<McpToolParameterDescriptor> parameters,
            JsonObject inputSchema,
            McpToolAnnotations annotations)
            : this(name, httpMethod, routeTemplate, parameters, inputSchema, annotations)
        {
            Action = action;
        }

        public McpToolDescriptor(
            string name,
            string httpMethod,
            string routeTemplate,
            IReadOnlyList<McpToolParameterDescriptor> parameters,
            JsonObject inputSchema,
            McpToolAnnotations annotations)
        {
            Name = name;
            HttpMethod = httpMethod;
            RouteTemplate = routeTemplate;
            Parameters = parameters;
            InputSchema = inputSchema;
            Annotations = annotations;
        }

        /// <summary>
        /// Describes a tool that is not backed by an HTTP endpoint - one contributed by an
        /// <see cref="IMcpToolSource"/>, such as a SignalR hub method. Such a tool has no verb or
        /// route, and is dispatched through <see cref="InvokerType"/> rather than the pipeline replay.
        /// </summary>
        public McpToolDescriptor(
            string name,
            IReadOnlyList<McpToolParameterDescriptor> parameters,
            JsonObject inputSchema,
            McpToolAnnotations annotations)
            : this(name, string.Empty, string.Empty, parameters, inputSchema, annotations)
        {
        }

        /// <summary>Tool name advertised over MCP.</summary>
        public string Name { get; }

        public string? Description { get; set; }

        /// <summary>Uppercase HTTP verb used for the synthetic request.</summary>
        public string HttpMethod { get; }

        /// <summary>Route template with binding constraints stripped, for example <c>api/todos/{id}</c>.</summary>
        public string RouteTemplate { get; }

        /// <summary>
        /// The MVC action behind the tool, or <c>null</c> when the tool was discovered from a Minimal
        /// API endpoint - see <c>Endpoint</c> in that case.
        /// </summary>
        public ControllerActionDescriptor? Action { get; }

#if !NETSTANDARD2_0
        /// <summary>
        /// The route endpoint behind the tool when it was discovered from a Minimal API route handler,
        /// or <c>null</c> for controller-backed tools - see <see cref="Action"/> in that case.
        /// </summary>
        public Microsoft.AspNetCore.Http.Endpoint? Endpoint { get; set; }
#endif

        /// <summary>Inputs the caller supplies. Excludes anything pinned through <see cref="Constants"/>.</summary>
        public IReadOnlyList<McpToolParameterDescriptor> Parameters { get; }

        public JsonObject InputSchema { get; }

        public McpToolAnnotations Annotations { get; }

        /// <summary>
        /// What the underlying action demands of its caller. Used to decide whether the tool is worth
        /// advertising to the caller of <c>tools/list</c>; it never replaces the authorization the
        /// pipeline performs when the tool is invoked.
        /// </summary>
        public McpToolAuthorization Authorization { get; set; } = McpToolAuthorization.None;

        /// <summary>
        /// Values fixed by the tool definition and written onto every request, on top of whatever the
        /// caller supplied. Populated from <see cref="McpToolAttribute.ConstantParameters"/>.
        /// </summary>
        public IReadOnlyList<McpToolConstantDescriptor> Constants { get; set; } =
            new McpToolConstantDescriptor[0];

        /// <summary>
        /// Route values needed by endpoint matching in addition to the ones taken from the template
        /// (<c>controller</c>, <c>action</c>, <c>area</c>, ...).
        /// </summary>
        public IReadOnlyDictionary<string, string?> ConstantRouteValues { get; set; } =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The <see cref="Execution.IMcpToolInvoker"/> implementation that runs this tool, resolved
        /// from the request's services. <c>null</c> - the default, and the value for every
        /// HTTP-discovered tool - means the standard pipeline-replay invoker.
        /// </summary>
        public Type? InvokerType { get; set; }

        public override string ToString() =>
            HttpMethod.Length == 0 ? Name : Name + " (" + HttpMethod + " /" + RouteTemplate + ")";
    }
}
