using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nabu.Mcp.AspNetCore.Discovery;

namespace Nabu.Mcp.AspNetCore.Server
{
    /// <summary>
    /// Answers the two authorization questions the MCP endpoint asks about a tool without invoking it:
    /// whether the action behind it demands anything of its caller at all, and whether this particular
    /// caller should be advertised it.
    /// </summary>
    /// <remarks>
    /// Replace the default implementation in DI when the application's authorization cannot be judged
    /// from <c>[Authorize]</c> metadata alone - for example when access depends on a tenant lookup, or
    /// on a requirement whose handler needs a resource that only the real endpoint can supply.
    /// </remarks>
    public interface IMcpToolAuthorizationEvaluator
    {
        /// <summary>
        /// Whether the action behind <paramref name="tool"/> is protected: it carries <c>[Authorize]</c>
        /// metadata, or it carries none and the application's fallback policy therefore applies. An
        /// action carrying <c>[AllowAnonymous]</c> is never protected.
        /// </summary>
        Task<bool> RequiresAuthorizationAsync(McpToolDescriptor tool, HttpContext context, CancellationToken cancellationToken);

        /// <summary>
        /// Returns <c>true</c> when <paramref name="tool"/> should appear in <c>tools/list</c> for the
        /// caller of <paramref name="context"/>.
        /// </summary>
        Task<bool> IsVisibleAsync(McpToolDescriptor tool, HttpContext context, CancellationToken cancellationToken);
    }
}
