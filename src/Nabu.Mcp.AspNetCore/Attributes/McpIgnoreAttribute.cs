using System;

namespace Nabu.Mcp.AspNetCore
{
    /// <summary>
    /// Excludes a controller, an action, an action parameter or a model property from MCP.
    /// Always wins over <see cref="McpToolAttribute"/> and over
    /// <see cref="NabuMcpOptions.ExposeAllActions"/>.
    /// </summary>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Parameter | AttributeTargets.Property,
        AllowMultiple = false,
        Inherited = true)]
    public sealed class McpIgnoreAttribute : Attribute
    {
    }
}
