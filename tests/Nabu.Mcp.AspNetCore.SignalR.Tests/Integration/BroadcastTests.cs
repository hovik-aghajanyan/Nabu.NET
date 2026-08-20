using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Nabu.Sample.ChatHub.Models;
using Xunit;

namespace Nabu.Mcp.AspNetCore.SignalR.Tests.Integration
{
    /// <summary>
    /// The differentiator: a tool call runs through the application's real hub, so its broadcasts
    /// reach the clients that are really connected.
    /// </summary>
    public class BroadcastTests : IClassFixture<ChatHubTestFixture>
    {
        private readonly ChatHubTestFixture _fixture;

        public BroadcastTests(ChatHubTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task A_message_sent_over_mcp_reaches_a_connected_signalr_client()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var received = new TaskCompletionSource<ChatMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            // A real SignalR client, connected to the in-memory server over the test handler.
            var connection = new HubConnectionBuilder()
                .WithUrl(_fixture.Server.BaseAddress + "hubs/chat", options =>
                {
                    options.HttpMessageHandlerFactory = _ => _fixture.Server.CreateHandler();
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token.Parameter);
                })
                .Build();

            connection.On<ChatMessage>("messageReceived", message => received.TrySetResult(message));

            await connection.StartAsync();
            try
            {
                var text = "broadcast " + Guid.NewGuid().ToString("N");
                await _fixture.CallToolAsync("chat_send_message", new JsonObject { ["text"] = text }, token);

                var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(text, message.Text);
                Assert.Equal("alice", message.User);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
