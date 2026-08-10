using System;

namespace Nabu.Mcp.AspNetCore
{
    /// <summary>
    /// Marks a controller action - or every action of a controller, when placed on the controller
    /// class - as a Model Context Protocol tool.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The action keeps behaving like a normal MVC action. Nabu does not call the method directly;
    /// it replays a synthetic HTTP request through the application pipeline, so filters,
    /// authentication, authorization, model binding and validation all still run.
    /// </para>
    /// <para>
    /// The attribute may be applied several times to the same action. Each occurrence publishes a
    /// separate tool over the same action, which is how one endpoint is exposed under several names
    /// with different parameter sets - see <see cref="IncludeParameters"/>,
    /// <see cref="ExcludeParameters"/> and <see cref="ConstantParameters"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// [HttpGet]
    /// [McpTool("todos_search", Description = "Search todo items with the full filter set.")]
    /// [McpTool("todos_list_overdue",
    ///     Description = "List the overdue todo items.",
    ///     ExcludeParameters = new[] { "search", "priority" },
    ///     ConstantParameters = new[] { "isCompleted=false", "pageSize=100" })]
    /// public ActionResult&lt;TodoPage&gt; List(bool? isCompleted, TodoPriority? priority, string? search, int page = 0, int pageSize = 20)
    /// </code>
    /// </example>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
    public sealed class McpToolAttribute : Attribute
    {
        private bool? _readOnly;
        private bool? _destructive;
        private bool? _idempotent;
        private bool? _openWorld;
        private bool? _requiresAuthorization;

        public McpToolAttribute()
        {
        }

        /// <param name="name">Explicit tool name. When omitted the name is derived from the controller and action.</param>
        public McpToolAttribute(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Tool name as advertised to MCP clients. When <c>null</c>, a name is generated from the
        /// controller and action names using <see cref="NabuMcpOptions.ToolNameFactory"/>.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>Human readable display name for the tool.</summary>
        public string? Title { get; set; }

        /// <summary>
        /// Description shown to the model. When omitted, Nabu falls back to the XML documentation
        /// <c>&lt;summary&gt;</c> of the action, and finally to a generated "HTTP VERB /route" string.
        /// </summary>
        public string? Description { get; set; }

        /// <summary>
        /// Set to <c>false</c> to keep the attribute in place but stop advertising the tool.
        /// Useful for feature flags applied at build time.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Restricts the tool to this set of inputs; everything else the action accepts is hidden and
        /// left unset. <c>null</c> or empty means "expose every input", which is the default.
        /// </summary>
        /// <remarks>
        /// Names match either the tool input name (camelCase, as it appears in the schema) or the
        /// underlying binding name, case-insensitively. <see cref="ConstantParameters"/> entries are
        /// applied regardless of this list.
        /// </remarks>
        public string[]? IncludeParameters { get; set; }

        /// <summary>
        /// Hides these inputs from the tool schema. They are left unset on the request, so the action's
        /// own defaults apply. Applied after <see cref="IncludeParameters"/>.
        /// </summary>
        public string[]? ExcludeParameters { get; set; }

        /// <summary>
        /// Pins inputs to fixed values, in <c>name=value</c> form (for example
        /// <c>"isCompleted=false"</c>). Pinned inputs disappear from the tool schema and the value is
        /// always sent on the underlying request, so a single action can back several narrower tools.
        /// </summary>
        /// <remarks>
        /// The value is converted to the parameter's CLR type: numbers and booleans become JSON
        /// numbers and booleans, complex parameters accept a JSON literal, and everything else is sent
        /// as a string. A bare name with no <c>=</c> pins the parameter to an empty string.
        /// </remarks>
        public string[]? ConstantParameters { get; set; }

        /// <summary>
        /// Marks these inputs as required for this tool even when the action treats them as optional.
        /// </summary>
        public string[]? RequiredParameters { get; set; }

        /// <summary>
        /// Marks these inputs as optional for this tool. Route tokens that the route template cannot
        /// do without stay required regardless.
        /// </summary>
        public string[]? OptionalParameters { get; set; }

        /// <summary>
        /// <c>readOnlyHint</c> annotation. Defaults to <c>true</c> for GET/HEAD actions.
        /// </summary>
        public bool ReadOnly
        {
            get { return _readOnly ?? false; }
            set { _readOnly = value; }
        }

        /// <summary>
        /// <c>destructiveHint</c> annotation. Defaults to <c>true</c> for DELETE and PUT actions.
        /// </summary>
        public bool Destructive
        {
            get { return _destructive ?? false; }
            set { _destructive = value; }
        }

        /// <summary>
        /// <c>idempotentHint</c> annotation. Defaults to <c>true</c> for GET/HEAD/PUT/DELETE actions.
        /// </summary>
        public bool Idempotent
        {
            get { return _idempotent ?? false; }
            set { _idempotent = value; }
        }

        /// <summary><c>openWorldHint</c> annotation. Defaults to <c>false</c>.</summary>
        public bool OpenWorld
        {
            get { return _openWorld ?? false; }
            set { _openWorld = value; }
        }

        /// <summary>
        /// Overrides whether the tool counts as protected when
        /// <see cref="NabuMcpOptions.ToolVisibility"/> tailors the advertised tool list to the caller.
        /// By default Nabu reads that from the <c>[Authorize]</c> and <c>[AllowAnonymous]</c> metadata of
        /// the action and its controller; set this when the action is protected by something Nabu cannot
        /// see, such as a custom filter or a gateway, or to keep a protected action advertised anyway.
        /// </summary>
        /// <remarks>
        /// This only affects what is advertised. Whether a call actually succeeds is decided by the
        /// application pipeline, which authorizes every tool call regardless of this value.
        /// </remarks>
        public bool RequiresAuthorization
        {
            get { return _requiresAuthorization ?? false; }
            set { _requiresAuthorization = value; }
        }

        internal bool? ReadOnlyOverride { get { return _readOnly; } }

        internal bool? DestructiveOverride { get { return _destructive; } }

        internal bool? IdempotentOverride { get { return _idempotent; } }

        internal bool? OpenWorldOverride { get { return _openWorld; } }

        internal bool? RequiresAuthorizationOverride { get { return _requiresAuthorization; } }
    }
}
