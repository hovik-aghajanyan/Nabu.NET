using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Nabu.Mcp.AspNetCore;
using Nabu.Sample.ChatHub.Models;
using Nabu.Sample.ChatHub.Services;

namespace Nabu.Sample.ChatHub.Hubs
{
    /// <summary>
    /// The chat room. Browser clients connect to it at <c>/hubs/chat</c>; the same methods are
    /// published as MCP tools, and a tool call runs through the very same hub - a message sent over
    /// MCP is broadcast to every connected browser.
    /// </summary>
    public class ChatHub : Hub
    {
        private readonly ChatHistory _history;

        public ChatHub(ChatHistory history)
        {
            _history = history;
        }

        /// <summary>Lists the most recent chat messages.</summary>
        /// <param name="count">How many messages to return, newest last.</param>
        [McpTool(ReadOnly = true, Idempotent = true)]
        public Task<IReadOnlyList<ChatMessage>> GetRecentMessages(int count = 20)
        {
            return Task.FromResult(_history.GetRecent(count));
        }

        /// <summary>Streams the most recent chat messages one by one, newest last.</summary>
        /// <param name="count">How many messages to stream.</param>
        [McpTool(ReadOnly = true, Idempotent = true)]
        public async IAsyncEnumerable<ChatMessage> StreamRecentMessages(
            int count = 20,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var message in _history.GetRecent(count))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
                await Task.Yield();
            }
        }

        /// <summary>Sends a message to the chat room. It is broadcast to every connected client.</summary>
        /// <param name="text">The message text.</param>
        [Authorize]
        [McpTool]
        public async Task<ChatMessage> SendMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new HubException("The message text must not be empty.");
            }

            var message = _history.Add(Context.User?.Identity?.Name ?? "anonymous", text.Trim());
            await Clients.All.SendAsync("messageReceived", message);
            return message;
        }

        /// <summary>Deletes a message from the room. Administrators only.</summary>
        /// <param name="id">Identifier of the message to delete.</param>
        [Authorize(Policy = "AdminOnly")]
        [McpTool(Destructive = true)]
        public async Task DeleteMessage(Guid id)
        {
            if (!_history.Delete(id))
            {
                throw new HubException("No message with id '" + id + "' exists.");
            }

            await Clients.All.SendAsync("messageDeleted", id);
        }

        /// <summary>
        /// Reports who the hub believes you are. The answer arrives as a message to the caller -
        /// over MCP it shows up in the tool result's <c>callerMessages</c>.
        /// </summary>
        [Authorize]
        [McpTool(ReadOnly = true, Idempotent = true)]
        public async Task WhoAmI()
        {
            await Clients.Caller.SendAsync("whoami", new
            {
                name = Context.User?.Identity?.Name,
                userIdentifier = Context.UserIdentifier,
                connectionId = Context.ConnectionId,
            });
        }
    }
}
