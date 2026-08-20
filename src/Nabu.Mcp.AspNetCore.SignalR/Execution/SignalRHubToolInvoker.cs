using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;

namespace Nabu.Mcp.AspNetCore.SignalR.Execution
{
    /// <summary>
    /// Runs a hub-method tool by opening a synthetic in-process SignalR connection through the
    /// application's own <c>HubConnectionHandler&lt;THub&gt;</c>.
    /// </summary>
    public sealed class SignalRHubToolInvoker : IMcpToolInvoker
    {
        public Task<McpToolInvocationResult> InvokeAsync(
            McpToolDescriptor tool,
            JsonObject? arguments,
            HttpContext originalContext,
            CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }
    }
}
