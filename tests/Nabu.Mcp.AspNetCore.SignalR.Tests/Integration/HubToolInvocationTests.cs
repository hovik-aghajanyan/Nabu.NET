using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.SignalR.Tests.Integration
{
    public class HubToolInvocationTests : IClassFixture<ChatHubTestFixture>
    {
        private readonly ChatHubTestFixture _fixture;

        public HubToolInvocationTests(ChatHubTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task A_hub_method_runs_and_returns_structured_content()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var text = "integration " + Guid.NewGuid().ToString("N");

            var result = await _fixture.CallToolAsync("chat_send_message", new JsonObject { ["text"] = text }, token);

            Assert.False(result["isError"]!.GetValue<bool>());
            var structured = result["structuredContent"]!.AsObject();
            Assert.Equal(text, structured["result"]!["text"]!.GetValue<string>());
            Assert.Equal("alice", structured["result"]!["user"]!.GetValue<string>());

            // The identity that reached the hub is the MCP caller's, end to end.
            var listed = await _fixture.CallToolAsync("chat_get_recent_messages", new JsonObject { ["count"] = 100 }, token);
            var texts = listed["structuredContent"]!["result"]!.AsArray().Select(m => m!["text"]!.GetValue<string>());
            Assert.Contains(text, texts);
        }

        [Fact]
        public async Task Caller_directed_messages_land_in_the_tool_result()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var result = await _fixture.CallToolAsync("chat_who_am_i", token: token);

            Assert.False(result["isError"]!.GetValue<bool>());
            var messages = result["structuredContent"]!["callerMessages"]!.AsArray();
            var whoami = messages.Single(m => m!["method"]!.GetValue<string>() == "whoami")!.AsObject();
            Assert.Equal("alice", whoami["arguments"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("alice", whoami["arguments"]![0]!["userIdentifier"]!.GetValue<string>());
        }

        [Fact]
        public async Task One_callers_identity_never_leaks_into_anothers_call()
        {
            var whoAlice = await _fixture.CallToolAsync("chat_who_am_i", token: await _fixture.GetTokenAsync("alice"));
            var whoRoot = await _fixture.CallToolAsync("chat_who_am_i", token: await _fixture.GetTokenAsync("root"));

            Assert.Equal("alice", whoAlice["structuredContent"]!["callerMessages"]![0]!["arguments"]![0]!["name"]!.GetValue<string>());
            Assert.Equal("root", whoRoot["structuredContent"]!["callerMessages"]![0]!["arguments"]![0]!["name"]!.GetValue<string>());
        }

        [Fact]
        public async Task Streaming_tools_collect_their_items()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var text = "stream-probe " + Guid.NewGuid().ToString("N");
            await _fixture.CallToolAsync("chat_send_message", new JsonObject { ["text"] = text }, token);

            var result = await _fixture.CallToolAsync("chat_stream_recent_messages", new JsonObject { ["count"] = 200 }, token);

            Assert.False(result["isError"]!.GetValue<bool>());
            var items = result["structuredContent"]!["streamItems"]!.AsArray();
            Assert.Contains(text, items.Select(item => item!["text"]!.GetValue<string>()));
        }

        [Fact]
        public async Task Hub_exceptions_become_tool_errors_with_their_message()
        {
            var token = await _fixture.GetTokenAsync("root");

            var result = await _fixture.CallToolAsync(
                "chat_delete_message",
                new JsonObject { ["id"] = Guid.NewGuid().ToString() },
                token);

            Assert.True(result["isError"]!.GetValue<bool>());
            var text = result["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
            Assert.Contains("No message with id", text);
        }

        [Fact]
        public async Task Missing_required_arguments_are_a_jsonrpc_error_not_a_tool_error()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var response = await _fixture.RpcAsync("tools/call", new JsonObject
            {
                ["name"] = "chat_send_message",
                ["arguments"] = new JsonObject(),
            }, token);

            Assert.Null(response["result"]);
            Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task Optional_arguments_fall_back_to_their_declared_defaults()
        {
            var result = await _fixture.CallToolAsync("chat_get_recent_messages");

            Assert.False(result["isError"]!.GetValue<bool>());
            Assert.True(result["structuredContent"]!["result"]!.AsArray().Count <= 20);
        }
    }
}
