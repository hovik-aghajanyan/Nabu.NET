using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nabu.Mcp.AspNetCore.Discovery;

namespace Nabu.Mcp.AspNetCore.Server
{
    /// <summary>
    /// Decides whether a tool is advertised to the caller of the current MCP request.
    /// </summary>
    /// <remarks>
    /// Replace the default implementation in DI when the application's authorization cannot be judged
    /// from <c>[Authorize]</c> metadata alone - for example when access depends on a tenant lookup, or
    /// on a requirement whose handler needs a resource that only the real endpoint can supply.
    /// </remarks>
    public interface IMcpToolAuthorizationEvaluator
    {
        /// <summary>
        /// Returns <c>true</c> when <paramref name="tool"/> should appear in <c>tools/list</c> for the
        /// caller of <paramref name="context"/>.
        /// </summary>
        Task<bool> IsVisibleAsync(McpToolDescriptor tool, HttpContext context, CancellationToken cancellationToken);
    }
}
