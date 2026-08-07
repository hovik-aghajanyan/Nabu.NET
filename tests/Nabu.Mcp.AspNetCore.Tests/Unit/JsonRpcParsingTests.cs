using System.Text.Json.Nodes;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.Protocol;
using Nabu.Mcp.AspNetCore.Server;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Unit
{
    public class JsonRpcParsingTests
    {
        [Fact]
        public void A_single_object_parses_to_one_request()
        {
            var payload = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");

            bool isBatch;
            var requests = McpRequestHandler.ParseRequests(payload, out isBatch);

            Assert.False(isBatch);
            Assert.Single(requests);
            Assert.Equal("tools/list", requests[0].Method);
            Assert.False(requests[0].IsNotification);
        }

        [Fact]
        public void An_array_parses_as_a_batch()
        {
            var payload = JsonNode.Parse("[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"},{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}]");

            bool isBatch;
            var requests = McpRequestHandler.ParseRequests(payload, out isBatch);

            Assert.True(isBatch);
            Assert.Equal(2, requests.Count);
            Assert.True(requests[1].IsNotification);
        }

        [Fact]
        public void A_message_without_an_id_is_a_notification()
        {
            var payload = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

            bool isBatch;
            var requests = McpRequestHandler.ParseRequests(payload, out isBatch);

            Assert.True(requests[0].IsNotification);
        }

        [Fact]
        public void An_explicit_null_id_is_also_a_notification()
        {
            var payload = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}");

            bool isBatch;
            var requests = McpRequestHandler.ParseRequests(payload, out isBatch);

            Assert.True(requests[0].IsNotification);
        }

        [Fact]
        public void A_message_without_a_method_is_rejected()
        {
            var payload = JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1}");

            bool isBatch;
            Assert.Throws<McpInvalidRequestException>(() => McpRequestHandler.ParseRequests(payload, out isBatch));
        }

        [Fact]
        public void A_non_object_message_is_rejected()
        {
            var payload = JsonNode.Parse("\"hello\"");

            bool isBatch;
            Assert.Throws<McpInvalidRequestException>(() => McpRequestHandler.ParseRequests(payload, out isBatch));
        }

        [Fact]
        public void Result_envelopes_carry_the_request_id()
        {
            var envelope = JsonRpc.Result(JsonValue.Create(7), new JsonObject { ["ok"] = true });

            Assert.Equal("2.0", envelope["jsonrpc"]!.GetValue<string>());
            Assert.Equal(7, envelope["id"]!.GetValue<int>());
            Assert.True(envelope["result"]!["ok"]!.GetValue<bool>());
        }

        [Fact]
        public void Error_envelopes_carry_the_code_and_message()
        {
            var envelope = JsonRpc.Error(JsonValue.Create("abc"), McpConstants.MethodNotFound, "nope");

            Assert.Equal("abc", envelope["id"]!.GetValue<string>());
            Assert.Equal(-32601, envelope["error"]!["code"]!.GetValue<int>());
            Assert.Equal("nope", envelope["error"]!["message"]!.GetValue<string>());
        }
    }
}
