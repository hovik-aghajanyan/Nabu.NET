using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// Protocol revision 2026-07-28: no handshake, no sessions, the version and capabilities
    /// declared in <c>_meta</c> on every request and mirrored into HTTP headers.
    /// </summary>
    [Collection(McpTestCollection.Name)]
    public class ModernProtocolTests
    {
        private const string Version = "2026-07-28";

        private readonly McpTestFixture _fixture;
        private int _nextId = 100;

        public ModernProtocolTests(McpTestFixture fixture)
        {
            _fixture = fixture;
        }

        private JsonObject BuildRequest(string method, JsonObject? parameters = null, string? version = Version)
        {
            parameters ??= new JsonObject();
            if (version != null)
            {
                parameters["_meta"] = new JsonObject
                {
                    ["io.modelcontextprotocol/protocolVersion"] = version,
                    ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject(),
                    ["io.modelcontextprotocol/clientInfo"] = new JsonObject
                    {
                        ["name"] = "nabu-tests",
                        ["version"] = "1.0.0",
                    },
                };
            }

            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = System.Threading.Interlocked.Increment(ref _nextId),
                ["method"] = method,
                ["params"] = parameters,
            };
        }

        private async Task<HttpResponseMessage> PostAsync(
            JsonObject request,
            AuthenticationHeaderValue? token,
            string? versionHeader,
            string? methodHeader,
            string? nameHeader = null)
        {
            var client = _fixture.CreateClient();
            client.DefaultRequestHeaders.Authorization = token;

            var message = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
            };

            if (versionHeader != null)
            {
                message.Headers.TryAddWithoutValidation("MCP-Protocol-Version", versionHeader);
            }

            if (methodHeader != null)
            {
                message.Headers.TryAddWithoutValidation("Mcp-Method", methodHeader);
            }

            if (nameHeader != null)
            {
                message.Headers.TryAddWithoutValidation("Mcp-Name", nameHeader);
            }

            return await client.SendAsync(message);
        }

        private async Task<HttpResponseMessage> PostConformingAsync(
            JsonObject request,
            AuthenticationHeaderValue? token,
            string? nameHeader = null)
        {
            return await PostAsync(
                request,
                token,
                request["params"]!["_meta"]?["io.modelcontextprotocol/protocolVersion"]?.GetValue<string>(),
                request["method"]!.GetValue<string>(),
                nameHeader);
        }

        private static async Task<JsonObject> BodyOf(HttpResponseMessage response)
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        [Fact]
        public async Task Server_discover_reports_versions_capabilities_and_identity()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await PostConformingAsync(BuildRequest("server/discover"), token);
            var result = (await BodyOf(response))["result"]!;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("complete", result["resultType"]!.GetValue<string>());
            Assert.Contains(Version, result["supportedVersions"]!.AsArray().GetValues<string>());
            Assert.NotNull(result["capabilities"]!["tools"]);
            Assert.Equal(
                "nabu-todo-sample",
                result["_meta"]!["io.modelcontextprotocol/serverInfo"]!["name"]!.GetValue<string>());
            Assert.True(result["ttlMs"]!.GetValue<int>() > 0);
            Assert.NotNull(result["cacheScope"]);
        }

        [Fact]
        public async Task Modern_tools_list_carries_the_cacheable_result_fields()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await PostConformingAsync(BuildRequest("tools/list"), token);
            var result = (await BodyOf(response))["result"]!;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("complete", result["resultType"]!.GetValue<string>());
            Assert.True(result["tools"]!.AsArray().Count > 0);
            Assert.True(result["ttlMs"]!.GetValue<int>() > 0);
            Assert.Equal("private", result["cacheScope"]!.GetValue<string>());
        }

        [Fact]
        public async Task Modern_tools_call_executes_the_action_and_stamps_the_result()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var request = BuildRequest(
                "tools/call",
                new JsonObject { ["name"] = "todos_list", ["arguments"] = new JsonObject() });

            using var response = await PostConformingAsync(request, token, nameHeader: "todos_list");
            var result = (await BodyOf(response))["result"]!.AsObject();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("complete", result["resultType"]!.GetValue<string>());
            Assert.False(McpTestFixture.IsError(result));
            Assert.NotNull(result["_meta"]!["io.modelcontextprotocol/serverInfo"]);
        }

        [Fact]
        public async Task Modern_requests_are_not_answered_with_a_session_id()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await PostConformingAsync(BuildRequest("server/discover"), token);

            Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        }

        [Fact]
        public async Task An_unsupported_version_is_rejected_with_the_supported_list()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await PostConformingAsync(BuildRequest("tools/list", version: "2025-06-18"), token);
            var error = (await BodyOf(response))["error"]!;

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(-32022, error["code"]!.GetValue<int>());
            Assert.Equal("2025-06-18", error["data"]!["requested"]!.GetValue<string>());
            Assert.Contains(Version, error["data"]!["supported"]!.AsArray().GetValues<string>());
        }

        [Fact]
        public async Task A_missing_protocol_version_header_is_a_header_mismatch()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await PostAsync(BuildRequest("tools/list"), token, versionHeader: null, methodHeader: "tools/list");
            var error = (await BodyOf(response))["error"]!;

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(-32020, error["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_method_header_that_disagrees_with_the_body_is_a_header_mismatch()
        {
            var token = await _fixture.GetTokenAsync("alice");

            using var response = await PostAsync(BuildRequest("tools/list"), token, Version, methodHeader: "tools/call");
            var error = (await BodyOf(response))["error"]!;

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(-32020, error["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_tools_call_without_the_name_header_is_a_header_mismatch()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var request = BuildRequest(
                "tools/call",
                new JsonObject { ["name"] = "todos_list", ["arguments"] = new JsonObject() });

            using var response = await PostAsync(request, token, Version, "tools/call", nameHeader: null);
            var error = (await BodyOf(response))["error"]!;

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(-32020, error["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_base64_encoded_name_header_is_decoded_before_comparison()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var request = BuildRequest(
                "tools/call",
                new JsonObject { ["name"] = "todos_list", ["arguments"] = new JsonObject() });

            var encoded = "=?base64?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("todos_list")) + "?=";
            using var response = await PostConformingAsync(request, token, nameHeader: encoded);
            var result = (await BodyOf(response))["result"]!.AsObject();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.False(McpTestFixture.IsError(result));
        }

        [Fact]
        public async Task Methods_removed_by_the_revision_get_404_with_method_not_found()
        {
            var token = await _fixture.GetTokenAsync("alice");

            // ping was removed in 2026-07-28; it only exists on the legacy path.
            using var response = await PostConformingAsync(BuildRequest("ping"), token);
            var error = (await BodyOf(response))["error"]!;

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(-32601, error["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task Modern_notifications_are_acknowledged_without_a_body()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var notification = BuildRequest("notifications/cancelled");
            notification.Remove("id");

            using var response = await PostConformingAsync(notification, token);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        }

        [Fact]
        public async Task Legacy_clients_still_negotiate_through_initialize()
        {
            var token = await _fixture.GetTokenAsync("alice");

            // No _meta declaration anywhere: the request must stay on the initialize/session path.
            var envelope = await _fixture.RpcAsync(
                "initialize",
                new JsonObject { ["protocolVersion"] = "2025-06-18" },
                token);

            Assert.Equal("2025-06-18", envelope["result"]!["protocolVersion"]!.GetValue<string>());
            Assert.Null(envelope["result"]!["resultType"]);
        }
    }
}
