using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nabu.Mcp.AspNetCore.Discovery;

namespace Nabu.Mcp.AspNetCore.Execution
{
    /// <summary>Executes a tool by replaying it as an HTTP request through the application pipeline.</summary>
    public interface IMcpToolInvoker
    {
        /// <param name="tool">The tool to run.</param>
        /// <param name="arguments">Arguments supplied by the MCP client.</param>
        /// <param name="originalContext">The HTTP context of the MCP request, used as the identity and transport template.</param>
        /// <param name="cancellationToken">Cancels the invocation.</param>
        Task<McpToolInvocationResult> InvokeAsync(
            McpToolDescriptor tool,
            JsonObject? arguments,
            HttpContext originalContext,
            CancellationToken cancellationToken);
    }
}
