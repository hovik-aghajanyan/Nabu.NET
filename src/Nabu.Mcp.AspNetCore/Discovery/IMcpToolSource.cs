using System.Collections.Generic;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>
    /// Contributes tools to the catalogue from somewhere other than the HTTP endpoint table -
    /// for example SignalR hub methods. Implementations are registered in DI and queried by
    /// <see cref="McpToolRegistry"/> whenever it rebuilds, after its own HTTP discovery.
    /// </summary>
    /// <remarks>
    /// A source owns the uniqueness of the names it hands out; a tool whose name is already taken
    /// by another source - or by an HTTP tool - is skipped with a warning rather than renamed.
    /// A contributed descriptor that is not HTTP-backed should set
    /// <see cref="McpToolDescriptor.InvokerType"/> so calls are dispatched to the source's own
    /// invoker instead of the HTTP pipeline replay.
    /// </remarks>
    public interface IMcpToolSource
    {
        /// <summary>The tools this source currently contributes.</summary>
        IReadOnlyList<McpToolDescriptor> GetTools();
    }
}
