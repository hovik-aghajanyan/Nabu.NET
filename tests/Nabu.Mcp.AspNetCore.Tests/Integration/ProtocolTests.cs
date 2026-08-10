using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    [Collection(McpTestCollection.Name)]
    public class ProtocolTests
    {
        private readonly McpTestFixture _fixture;

        public ProtocolTests(McpTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Initialize_reports_the_server_and_its_tool_capability()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var envelope = await _fixture.RpcAsync(
                "initialize",
                new JsonObject { ["protocolVersion"] = "2025-06-18" },
                token);

            var result = envelope["result"]!;
            Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
            Assert.Equal("nabu-todo-sample", result["serverInfo"]!["name"]!.GetValue<string>());
            Assert.NotNull(result["capabilities"]!["tools"]);
            Assert.NotNull(result["instructions"]);
        }

        [Fact]
        public async Task Initialize_negotiates_down_to_a_protocol_the_client_asked_for()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var envelope = await _fixture.RpcAsync(
                "initialize",
                new JsonObject { ["protocolVersion"] = "2024-11-05" },
                token);

            Assert.Equal("2024-11-05", envelope["result"]!["protocolVersion"]!.GetValue<string>());
        }

        [Fact]
        public async Task Initialize_falls_back_to_the_latest_supported_version()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var envelope = await _fixture.RpcAsync(
                "initialize",
                new JsonObject { ["protocolVersion"] = "1999-01-01" },
                token);

            Assert.Equal("2025-06-18", envelope["result"]!["protocolVersion"]!.GetValue<string>());
        }

        [Fact]
        public async Task Initialize_returns_a_session_id_header()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await _fixture.PostRawAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\"}}",
                token);

            Assert.True(response.Headers.Contains("Mcp-Session-Id"));
        }

        [Fact]
        public async Task Ping_is_answered_with_an_empty_result()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var envelope = await _fixture.RpcAsync("ping", token: token);

            Assert.NotNull(envelope["result"]);
            Assert.Null(envelope["error"]);
        }

        [Fact]
        public async Task Notifications_are_acknowledged_without_a_body()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await _fixture.PostRawAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                token);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Batches_answer_every_request_and_skip_notifications()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await _fixture.PostRawAsync(
                "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}," +
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}," +
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}]",
                token);

            var array = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();

            Assert.Equal(2, array.Count);
            Assert.Equal(new[] { 1, 2 }, array.Select(n => n!["id"]!.GetValue<int>()).ToArray());
        }

        [Fact]
        public async Task Unknown_methods_produce_a_method_not_found_error()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var envelope = await _fixture.RpcAsync("does/not/exist", token: token);

            Assert.Equal(-32601, envelope["error"]!["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task Unknown_notifications_are_silently_accepted()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await _fixture.PostRawAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}",
                token);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        [Fact]
        public async Task Resource_and_prompt_listings_are_answered_as_empty()
        {
            var token = await _fixture.GetTokenAsync("alice");

            Assert.Empty((await _fixture.RpcAsync("resources/list", token: token))["result"]!["resources"]!.AsArray());
            Assert.Empty((await _fixture.RpcAsync("prompts/list", token: token))["result"]!["prompts"]!.AsArray());
        }

        [Fact]
        public async Task Malformed_json_produces_a_parse_error()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await _fixture.PostRawAsync("{not json", token);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(-32700, envelope["error"]!["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_message_without_a_method_produces_an_invalid_request_error()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await _fixture.PostRawAsync("{\"jsonrpc\":\"2.0\",\"id\":1}", token);
            var envelope = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(-32600, envelope["error"]!["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task Get_is_not_allowed_on_the_endpoint()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var client = _fixture.CreateClient();
            client.DefaultRequestHeaders.Authorization = token;

            using var response = await client.GetAsync("/mcp");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Contains("POST", response.Content.Headers.Allow);
        }

        [Fact]
        public async Task Get_without_credentials_answers_405_not_401()
        {
            // Streamable HTTP clients (VS Code among them) probe the endpoint with a GET to open
            // the server-to-client stream. A 401 here would send them into OAuth discovery; the
            // spec's answer for an unsupported verb is 405.
            var client = _fixture.CreateClient();

            using var response = await client.GetAsync("/mcp");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Contains("POST", response.Content.Headers.Allow);
        }

        [Fact]
        public async Task Delete_terminates_the_session_without_error()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var client = _fixture.CreateClient();
            client.DefaultRequestHeaders.Authorization = token;

            using var response = await client.DeleteAsync("/mcp");

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        [Fact]
        public async Task An_event_stream_client_receives_a_server_sent_event()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var client = _fixture.CreateClient();
            client.DefaultRequestHeaders.Authorization = token;

            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(
                    "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("event: message", body);
            Assert.Contains("\"id\":1", body);
        }

        [Fact]
        public async Task A_json_client_receives_application_json()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await _fixture.PostRawAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}", token);

            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        }
    }
}
