using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.SignalR.Tests.Integration
{
    /// <summary>
    /// The repository's signature claim, applied to hubs: the same authorization attributes that
    /// protect a hub for live SignalR clients protect it over MCP, for the same caller.
    /// </summary>
    public class HubAuthorizationTests : IClassFixture<ChatHubTestFixture>
    {
        private readonly ChatHubTestFixture _fixture;

        public HubAuthorizationTests(ChatHubTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Method_level_policies_are_enforced_by_the_real_dispatcher()
        {
            // alice is authenticated but not an administrator; the dispatcher itself refuses her.
            var alice = await _fixture.CallToolAsync(
                "chat_delete_message",
                new JsonObject { ["id"] = Guid.NewGuid().ToString() },
                await _fixture.GetTokenAsync("alice"));

            Assert.True(alice["isError"]!.GetValue<bool>());
            Assert.Contains("unauthorized", alice["content"]!.AsArray()[0]!["text"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task The_same_admin_only_method_succeeds_for_an_administrator()
        {
            var root = await _fixture.GetTokenAsync("root");

            var sent = await _fixture.CallToolAsync(
                "chat_send_message",
                new JsonObject { ["text"] = "to be deleted" },
                root);
            var id = sent["structuredContent"]!["result"]!["id"]!.GetValue<string>();

            var deleted = await _fixture.CallToolAsync("chat_delete_message", new JsonObject { ["id"] = id }, root);

            Assert.False(deleted["isError"]!.GetValue<bool>());
        }

        [Fact]
        public async Task A_class_level_authorize_gates_every_tool_of_the_hub()
        {
            // The presence hub carries [Authorize] on the class and nothing on the method. A live
            // anonymous client could not even connect; over MCP the same caller is refused too.
            // (The endpoint itself already challenges anonymous callers for protected tools, so this
            // asserts the visible half: the tool is not advertised without credentials...)
            var anonymous = await _fixture.ListToolNamesAsync();
            Assert.DoesNotContain("presence_list_online_users", anonymous);

            // ...and works for an authenticated caller.
            var result = await _fixture.CallToolAsync(
                "presence_list_online_users",
                token: await _fixture.GetTokenAsync("alice"));

            Assert.False(result["isError"]!.GetValue<bool>());
        }
    }
}
