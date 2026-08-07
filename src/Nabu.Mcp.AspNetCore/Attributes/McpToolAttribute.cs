using System;

namespace Nabu.Mcp.AspNetCore
{
    /// <summary>
    /// Marks a controller action - or every action of a controller, when placed on the controller
    /// class - as a Model Context Protocol tool.
    /// </summary>
    /// <remarks>
    /// The action keeps behaving like a normal MVC action. Nabu does not call the method directly;
    /// it replays a synthetic HTTP request through the application pipeline, so filters,
    /// authentication, authorization, model binding and validation all still run.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class McpToolAttribute : Attribute
    {
        private bool? _readOnly;
        private bool? _destructive;
        private bool? _idempotent;
        private bool? _openWorld;

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

        internal bool? ReadOnlyOverride { get { return _readOnly; } }

        internal bool? DestructiveOverride { get { return _destructive; } }

        internal bool? IdempotentOverride { get { return _idempotent; } }

        internal bool? OpenWorldOverride { get { return _openWorld; } }
    }
}
