using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>The set of controller actions currently exposed as MCP tools.</summary>
    public interface IMcpToolRegistry
    {
        /// <summary>All tools, ordered by name.</summary>
        IReadOnlyList<McpToolDescriptor> GetTools();

        /// <summary>Looks a tool up by its advertised name.</summary>
        bool TryGetTool(string name, [NotNullWhen(true)] out McpToolDescriptor? tool);
    }
}
