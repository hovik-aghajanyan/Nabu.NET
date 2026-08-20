using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nabu.Mcp.AspNetCore;

namespace Nabu.Sample.ChatHub.Hubs
{
    /// <summary>
    /// Demonstrates the hub-class-level <c>[Authorize]</c>: on a live connection it is enforced at
    /// the negotiate, so an anonymous caller cannot connect at all - and over MCP the invoker
    /// enforces the same gate before opening the synthetic connection, so anonymous callers are
    /// refused every tool on this hub.
    /// </summary>
    [Authorize]
    public class PresenceHub : Hub
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, string> Online = new Dictionary<string, string>();

        /// <summary>Lists the users currently connected to the presence hub.</summary>
        [McpTool(ReadOnly = true, Idempotent = true)]
        public Task<string[]> ListOnlineUsers()
        {
            lock (Sync)
            {
                return Task.FromResult(Online.Values.Distinct().OrderBy(name => name).ToArray());
            }
        }

        public override async Task OnConnectedAsync()
        {
            lock (Sync)
            {
                Online[Context.ConnectionId] = Context.User?.Identity?.Name ?? "unknown";
            }

            await Clients.Others.SendAsync("userJoined", Context.User?.Identity?.Name);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(System.Exception? exception)
        {
            lock (Sync)
            {
                Online.Remove(Context.ConnectionId);
            }

            await Clients.Others.SendAsync("userLeft", Context.User?.Identity?.Name);
            await base.OnDisconnectedAsync(exception);
        }
    }
}
