using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// The point of the framework: a tool call must be subject to exactly the same authentication and
    /// authorization as the equivalent HTTP request.
    /// </summary>
    [Collection(McpTestCollection.Name)]
    public class AuthorizationTests
    {
        private readonly McpTestFixture _fixture;

        public AuthorizationTests(McpTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task The_mcp_endpoint_rejects_anonymous_callers()
        {
            using var response = await _fixture.PostRawAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task The_mcp_endpoint_rejects_a_bogus_token()
        {
            var token = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-real-token");

            using var response = await _fixture.PostRawAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}",
                token);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public async Task A_policy_protected_action_is_refused_for_a_caller_without_the_role()
        {
            var alice = await _fixture.GetTokenAsync("alice");

            var created = await _fixture.CallToolAsync(
                "todos_create",
                new JsonObject { ["title"] = "Policy check" },
                alice);
            var id = McpTestFixture.JsonOf(created)["id"]!.GetValue<string>();

            var result = await _fixture.CallToolAsync("todos_delete", new JsonObject { ["id"] = id }, alice);

            Assert.True(McpTestFixture.IsError(result));
            Assert.Contains("403", McpTestFixture.TextOf(result));
        }

        [Fact]
        public async Task A_policy_protected_action_succeeds_for_a_caller_with_the_role()
        {
            var root = await _fixture.GetTokenAsync("root");

            var created = await _fixture.CallToolAsync(
                "todos_create",
                new JsonObject { ["title"] = "Admin delete check" },
                root);
            var id = McpTestFixture.JsonOf(created)["id"]!.GetValue<string>();

            var result = await _fixture.CallToolAsync("todos_delete", new JsonObject { ["id"] = id }, root);

            Assert.False(McpTestFixture.IsError(result));
        }

        [Fact]
        public async Task Tools_see_the_identity_of_the_caller_not_a_shared_one()
        {
            var alice = await _fixture.GetTokenAsync("alice");
            var root = await _fixture.GetTokenAsync("root");

            await _fixture.CallToolAsync("todos_create", new JsonObject { ["title"] = "alice-only marker" }, alice);

            var rootItems = McpTestFixture.JsonOf(await _fixture.CallToolAsync("todos_list", new JsonObject(), root))
                .AsObject()["items"]!.AsArray()
                .Select(i => i!["title"]!.GetValue<string>())
                .ToArray();

            var aliceItems = McpTestFixture.JsonOf(await _fixture.CallToolAsync("todos_list", new JsonObject(), alice))
                .AsObject()["items"]!.AsArray()
                .Select(i => i!["title"]!.GetValue<string>())
                .ToArray();

            Assert.Contains("alice-only marker", aliceItems);
            Assert.DoesNotContain("alice-only marker", rootItems);
        }

        [Fact]
        public async Task Every_item_returned_to_a_caller_belongs_to_that_caller()
        {
            var alice = await _fixture.GetTokenAsync("alice");

            var items = McpTestFixture.JsonOf(await _fixture.CallToolAsync("todos_list", new JsonObject(), alice))
                .AsObject()["items"]!.AsArray();

            Assert.All(items, item => Assert.Equal("alice", item!["owner"]!.GetValue<string>()));
        }

        [Fact]
        public async Task An_anonymous_action_still_works_through_an_authenticated_mcp_session()
        {
            var alice = await _fixture.GetTokenAsync("alice");

            var result = await _fixture.CallToolAsync("weather_get_cities", new JsonObject(), alice);

            Assert.False(McpTestFixture.IsError(result));
            Assert.Contains("Yerevan", McpTestFixture.TextOf(result));
        }
    }
}
