using Microsoft.AspNetCore.Builder;

namespace Nabu.Mcp.AspNetCore.Server
{
    /// <summary>
    /// Replaces the built-in protocol endpoint. When a mount is registered,
    /// <c>UseNabuMcp()</c> hands it the resolved endpoint path instead of adding the built-in
    /// middleware, so an alternative MCP protocol implementation (such as the official SDK, via
    /// the <c>Nabu.Mcp.ModelContextProtocol</c> package) can serve Nabu's tools. Discovery,
    /// schema generation and pipeline replay are unaffected - only the wire protocol changes.
    /// </summary>
    public interface INabuMcpProtocolMount
    {
        /// <summary>Mounts the protocol endpoint at <paramref name="path"/>.</summary>
        void Mount(IApplicationBuilder app, string path);
    }
}
