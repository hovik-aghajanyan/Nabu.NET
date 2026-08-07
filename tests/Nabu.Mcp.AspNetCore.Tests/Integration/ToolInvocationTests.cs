using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    [Collection(McpTestCollection.Name)]
    public class ToolInvocationTests
    {
        private readonly McpTestFixture _fixture;

        public ToolInvocationTests(McpTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task A_read_tool_returns_the_action_result_as_text_and_structured_content()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var result = await _fixture.CallToolAsync("todos_list", new JsonObject(), token);

            Assert.False(McpTestFixture.IsError(result));

            var payload = McpTestFixture.JsonOf(result).AsObject();
            Assert.True(payload.ContainsKey("items"));
            Assert.True(payload.ContainsKey("totalCount"));

            var structured = result["structuredContent"]!.AsObject();
            Assert.True(structured.ContainsKey("items"));
        }

        [Fact]
        public async Task Query_arguments_reach_the_action()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var result = await _fixture.CallToolAsync(
                "todos_list",
                new JsonObject { ["search"] = "design", ["pageSize"] = 5 },
                token);

            var payload = McpTestFixture.JsonOf(result).AsObject();
            var items = payload["items"]!.AsArray();

            Assert.Equal(5, payload["pageSize"]!.GetValue<int>());
            Assert.All(items, item => Assert.Contains("design", item!["title"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Route_arguments_reach_the_action()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var created = await CreateTodoAsync("Route binding check", token);
            var id = created["id"]!.GetValue<string>();

            var result = await _fixture.CallToolAsync("todos_get_by_id", new JsonObject { ["id"] = id }, token);

            Assert.False(McpTestFixture.IsError(result));
            Assert.Equal(id, McpTestFixture.JsonOf(result)["id"]!.GetValue<string>());
        }

        [Fact]
        public async Task Body_arguments_reach_the_action_including_enums_and_collections()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var result = await _fixture.CallToolAsync(
                "todos_create",
                new JsonObject
                {
                    ["title"] = "Body binding check",
                    ["notes"] = "with notes",
                    ["priority"] = "Critical",
                    ["tags"] = new JsonArray("a", "b"),
                },
                token);

            Assert.False(McpTestFixture.IsError(result));

            var created = McpTestFixture.JsonOf(result).AsObject();
            Assert.Equal("Body binding check", created["title"]!.GetValue<string>());
            Assert.Equal("with notes", created["notes"]!.GetValue<string>());
            Assert.Equal(new[] { "a", "b" }, created["tags"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray());
        }

        [Fact]
        public async Task Enum_names_are_translated_into_what_the_api_actually_accepts()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var created = await _fixture.CallToolAsync(
                "todos_create",
                new JsonObject { ["title"] = "Enum check", ["priority"] = "Critical" },
                token);

            // The sample serializes enums as numbers; Critical is 3.
            Assert.Equal(3, McpTestFixture.JsonOf(created)["priority"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_patch_tool_updates_only_the_supplied_fields()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var created = await CreateTodoAsync("Patch check", token);
            var id = created["id"]!.GetValue<string>();

            var result = await _fixture.CallToolAsync(
                "todos_update",
                new JsonObject { ["id"] = id, ["isCompleted"] = true },
                token);

            var updated = McpTestFixture.JsonOf(result).AsObject();
            Assert.True(updated["isCompleted"]!.GetValue<bool>());
            Assert.Equal("Patch check", updated["title"]!.GetValue<string>());
        }

        [Fact]
        public async Task Model_validation_failures_surface_as_tool_errors()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var result = await _fixture.CallToolAsync(
                "todos_create",
                new JsonObject { ["title"] = string.Empty },
                token);

            Assert.True(McpTestFixture.IsError(result));
            Assert.Contains("400", McpTestFixture.TextOf(result));
        }

        [Fact]
        public async Task Not_found_responses_surface_as_tool_errors()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var result = await _fixture.CallToolAsync(
                "todos_get_by_id",
                new JsonObject { ["id"] = Guid.NewGuid().ToString() },
                token);

            Assert.True(McpTestFixture.IsError(result));
            Assert.Contains("404", McpTestFixture.TextOf(result));
        }

        [Fact]
        public async Task An_empty_body_response_still_produces_readable_content()
        {
            var token = await _fixture.GetTokenAsync("root");
            var created = await CreateTodoAsync("Delete me", token);

            var result = await _fixture.CallToolAsync(
                "todos_delete",
                new JsonObject { ["id"] = created["id"]!.GetValue<string>() },
                token);

            Assert.False(McpTestFixture.IsError(result));
            Assert.Contains("204", McpTestFixture.TextOf(result));
        }

        [Fact]
        public async Task Missing_required_arguments_produce_a_json_rpc_error()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var envelope = await _fixture.RpcAsync(
                "tools/call",
                new JsonObject { ["name"] = "todos_get_by_id", ["arguments"] = new JsonObject() },
                token);

            Assert.Equal(-32602, envelope["error"]!["code"]!.GetValue<int>());
            Assert.Contains("id", envelope["error"]!["message"]!.GetValue<string>());
        }

        [Fact]
        public async Task Unknown_tools_produce_a_json_rpc_error()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var envelope = await _fixture.RpcAsync(
                "tools/call",
                new JsonObject { ["name"] = "not_a_tool", ["arguments"] = new JsonObject() },
                token);

            Assert.Equal(-32602, envelope["error"]!["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task Ignored_actions_cannot_be_reached_by_name()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var envelope = await _fixture.RpcAsync(
                "tools/call",
                new JsonObject { ["name"] = "todos_reindex", ["arguments"] = new JsonObject() },
                token);

            Assert.NotNull(envelope["error"]);
        }

        [Fact]
        public async Task Tool_calls_do_not_disturb_the_ordinary_http_surface()
        {
            var token = await _fixture.GetTokenAsync("alice");

            await _fixture.CallToolAsync("todos_list", new JsonObject(), token);

            using var client = _fixture.CreateClient();
            client.DefaultRequestHeaders.Authorization = token;

            using var response = await client.GetAsync("/api/todos");
            response.EnsureSuccessStatusCode();

            var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.NotNull(payload["items"]);
        }

        [Fact]
        public async Task Route_and_query_arguments_can_be_combined()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var result = await _fixture.CallToolAsync(
                "weather_get_forecast",
                new JsonObject { ["city"] = "Yerevan", ["days"] = 2 },
                token);

            var forecast = McpTestFixture.JsonOf(result).AsArray();
            Assert.Equal(2, forecast.Count);
        }

        [Fact]
        public async Task Route_values_are_url_encoded_rather_than_injected()
        {
            var token = await _fixture.GetTokenAsync("alice");

            // A slash in an argument must stay inside the segment instead of creating a new one.
            var result = await _fixture.CallToolAsync(
                "weather_get_forecast",
                new JsonObject { ["city"] = "New York/Brooklyn", ["days"] = 1 },
                token);

            Assert.False(McpTestFixture.IsError(result));
            Assert.Single(McpTestFixture.JsonOf(result).AsArray());
        }

        private async Task<JsonObject> CreateTodoAsync(string title, System.Net.Http.Headers.AuthenticationHeaderValue token)
        {
            var result = await _fixture.CallToolAsync(
                "todos_create",
                new JsonObject { ["title"] = title },
                token);

            Assert.False(McpTestFixture.IsError(result), McpTestFixture.TextOf(result));
            return McpTestFixture.JsonOf(result).AsObject();
        }
    }
}
