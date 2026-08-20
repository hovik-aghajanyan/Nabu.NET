using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.SignalR.Tests.Integration
{
    /// <summary>
    /// The catalogue: hub tools merge with HTTP tools at one endpoint, and each caller is shown
    /// exactly what its own credentials reach.
    /// </summary>
    public class HubToolCatalogueTests : IClassFixture<ChatHubTestFixture>
    {
        private readonly ChatHubTestFixture _fixture;

        public HubToolCatalogueTests(ChatHubTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Hub_tools_and_http_tools_share_one_catalogue()
        {
            var token = await _fixture.GetTokenAsync("root");
            var names = await _fixture.ListToolNamesAsync(token);

            // SignalR-backed tools...
            Assert.Contains("chat_send_message", names);
            Assert.Contains("presence_list_online_users", names);

            // ...next to an ordinary Minimal API tool from the core package.
            Assert.Contains("chat_room_info", names);
        }

        [Fact]
        public async Task Anonymous_callers_see_only_the_anonymous_hub_tools()
        {
            var names = await _fixture.ListToolNamesAsync();

            Assert.Contains("chat_get_recent_messages", names);
            Assert.Contains("chat_stream_recent_messages", names);
            Assert.DoesNotContain("chat_send_message", names);
            Assert.DoesNotContain("chat_delete_message", names);
            Assert.DoesNotContain("chat_who_am_i", names);

            // The whole presence hub is gated by its class-level [Authorize].
            Assert.DoesNotContain("presence_list_online_users", names);
        }

        [Fact]
        public async Task The_catalogue_grows_with_the_callers_credentials()
        {
            var anonymous = await _fixture.ListToolNamesAsync();
            var alice = await _fixture.ListToolNamesAsync(await _fixture.GetTokenAsync("alice"));
            var root = await _fixture.ListToolNamesAsync(await _fixture.GetTokenAsync("root"));

            Assert.True(anonymous.Length < alice.Length);
            Assert.True(alice.Length < root.Length);

            // Only the administrator is shown the admin-only tool.
            Assert.DoesNotContain("chat_delete_message", alice);
            Assert.Contains("chat_delete_message", root);
        }

        [Fact]
        public async Task Hub_tool_schemas_advertise_their_parameters()
        {
            var response = await _fixture.RpcAsync("tools/list", token: await _fixture.GetTokenAsync("alice"));
            var tool = response["result"]!["tools"]!.AsArray()
                .Single(t => t!["name"]!.GetValue<string>() == "chat_send_message")!.AsObject();

            var properties = tool["inputSchema"]!["properties"]!.AsObject();
            Assert.True(properties.ContainsKey("text"));
            Assert.Equal("The message text.", properties["text"]!["description"]!.GetValue<string>());
            var required = tool["inputSchema"]!["required"]!.AsArray();
            Assert.Contains("text", required.Select(node => node!.GetValue<string>()));
        }

        [Fact]
        public async Task Unauthenticated_calls_to_protected_hub_tools_are_challenged()
        {
            using var response = await _fixture.PostRawAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"chat_send_message\",\"arguments\":{\"text\":\"x\"}}}");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
