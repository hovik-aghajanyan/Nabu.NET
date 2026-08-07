using System;

namespace Nabu.Mcp.AspNetCore
{
    /// <summary>
    /// Refines how a single action parameter - or a property of a request model - is described in the
    /// generated JSON schema.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class McpParameterAttribute : Attribute
    {
        private bool? _required;

        public McpParameterAttribute()
        {
        }

        /// <param name="description">Description surfaced to the model for this parameter.</param>
        public McpParameterAttribute(string description)
        {
            Description = description;
        }

        /// <summary>Overrides the JSON property name used in the tool input schema.</summary>
        public string? Name { get; set; }

        /// <summary>Description surfaced to the model.</summary>
        public string? Description { get; set; }

        /// <summary>
        /// Forces the parameter into (or out of) the schema's <c>required</c> list. When unset, Nabu
        /// infers it from route membership, nullability, default values and <c>[Required]</c>.
        /// </summary>
        public bool Required
        {
            get { return _required ?? false; }
            set { _required = value; }
        }

        /// <summary>Example value rendered into the schema as <c>examples</c>.</summary>
        public object? Example { get; set; }

        internal bool? RequiredOverride { get { return _required; } }
    }
}
