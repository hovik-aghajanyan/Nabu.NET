using System.Linq;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nabu.Mcp.AspNetCore.Discovery;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    [Collection(McpTestCollection.Name)]
    public class ToolDiscoveryTests
    {
        private readonly McpTestFixture _fixture;

        public ToolDiscoveryTests(McpTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<JsonArray> ListToolsAsync()
        {
            // As an administrator, so that discovery is measured against the whole catalogue rather than
            // the subset one caller happens to be authorized for.
            var token = await _fixture.GetTokenAsync("root");
            var envelope = await _fixture.RpcAsync("tools/list", token: token);
            return envelope["result"]!["tools"]!.AsArray();
        }

        private static JsonObject Find(JsonArray tools, string name)
        {
            var tool = tools.FirstOrDefault(t => t!["name"]!.GetValue<string>() == name);
            Assert.NotNull(tool);
            return tool!.AsObject();
        }

        [Fact]
        public async Task Actions_marked_with_McpTool_are_published()
        {
            var tools = await ListToolsAsync();
            var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();

            Assert.Contains("todos_list", names);
            Assert.Contains("todos_get_by_id", names);
            Assert.Contains("todos_create", names);
            Assert.Contains("todos_update", names);
            Assert.Contains("todos_delete", names);
        }

        [Fact]
        public async Task A_controller_level_attribute_publishes_every_action_on_it()
        {
            var tools = await ListToolsAsync();
            var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();

            Assert.Contains("weather_get_forecast", names);
            Assert.Contains("weather_get_cities", names);
        }

        [Fact]
        public async Task A_minimal_api_endpoint_published_with_McpTool_is_discovered_and_callable()
        {
            var tools = await ListToolsAsync();
            var tool = Find(tools, "server_time_now");

            Assert.Equal("Reports the server's current UTC time.", tool["description"]!.GetValue<string>());

            var envelope = await _fixture.RpcAsync(
                "tools/call",
                new JsonObject { ["name"] = "server_time_now", ["arguments"] = new JsonObject() },
                token: await _fixture.GetTokenAsync("alice"));

            var result = envelope["result"]!.AsObject();
            Assert.False(result["isError"]?.GetValue<bool>() ?? false);
            Assert.NotNull(result["structuredContent"]!["utcNow"]);
        }

        [Fact]
        public async Task Actions_marked_with_McpIgnore_are_not_published()
        {
            var tools = await ListToolsAsync();
            var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();

            Assert.DoesNotContain("todos_reindex", names);
        }

        [Fact]
        public async Task A_controller_marked_with_McpIgnore_contributes_nothing()
        {
            var tools = await ListToolsAsync();
            var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();

            Assert.DoesNotContain(names, n => n.StartsWith("auth_"));
        }

        [Fact]
        public async Task Descriptions_come_from_the_xml_documentation()
        {
            var tools = await ListToolsAsync();

            Assert.Equal(
                "Lists the todo items belonging to the signed-in user.",
                Find(tools, "todos_list")["description"]!.GetValue<string>());
        }

        [Fact]
        public async Task Parameter_descriptions_come_from_the_xml_documentation()
        {
            var tools = await ListToolsAsync();
            var schema = Find(tools, "todos_list")["inputSchema"]!["properties"]!["search"]!;

            Assert.Equal(
                "Case-insensitive substring matched against the title and notes.",
                schema["description"]!.GetValue<string>());
        }

        [Fact]
        public async Task Route_parameters_are_required_and_typed()
        {
            var tools = await ListToolsAsync();
            var schema = Find(tools, "todos_get_by_id")["inputSchema"]!.AsObject();

            Assert.Equal("uuid", schema["properties"]!["id"]!["format"]!.GetValue<string>());
            Assert.Contains("id", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        }

        [Fact]
        public async Task Body_models_are_flattened_into_the_tool_schema()
        {
            var tools = await ListToolsAsync();
            var schema = Find(tools, "todos_create")["inputSchema"]!.AsObject();
            var properties = schema["properties"]!.AsObject();

            Assert.True(properties.ContainsKey("title"));
            Assert.True(properties.ContainsKey("priority"));
            Assert.True(properties.ContainsKey("tags"));
            Assert.False(properties.ContainsKey("request"));
            Assert.Contains("title", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        }

        [Fact]
        public async Task Validation_attributes_are_carried_into_the_schema()
        {
            var tools = await ListToolsAsync();
            var title = Find(tools, "todos_create")["inputSchema"]!["properties"]!["title"]!;

            Assert.Equal(1, title["minLength"]!.GetValue<int>());
            Assert.Equal(120, title["maxLength"]!.GetValue<int>());
        }

        [Fact]
        public async Task Annotations_are_derived_from_the_http_verb()
        {
            var tools = await ListToolsAsync();

            var list = Find(tools, "todos_list")["annotations"]!;
            Assert.True(list["readOnlyHint"]!.GetValue<bool>());
            Assert.False(list["destructiveHint"]!.GetValue<bool>());

            var delete = Find(tools, "todos_delete")["annotations"]!;
            Assert.False(delete["readOnlyHint"]!.GetValue<bool>());
            Assert.True(delete["destructiveHint"]!.GetValue<bool>());
        }

        [Fact]
        public async Task Attribute_supplied_annotation_overrides_win()
        {
            var tools = await ListToolsAsync();

            // [McpTool(Idempotent = false)] on a POST action.
            Assert.False(Find(tools, "todos_create")["annotations"]!["idempotentHint"]!.GetValue<bool>());

            // [McpTool(Destructive = false)] on a PATCH action.
            Assert.False(Find(tools, "todos_update")["annotations"]!["destructiveHint"]!.GetValue<bool>());
        }

        [Fact]
        public void The_registry_resolves_descriptors_with_their_route_and_verb()
        {
            using var scope = _fixture.Services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IMcpToolRegistry>();

            McpToolDescriptor? tool;
            Assert.True(registry.TryGetTool("todos_get_by_id", out tool));
            Assert.Equal("GET", tool!.HttpMethod);
            Assert.Equal("api/todos/{id}", tool.RouteTemplate);

            Assert.True(registry.TryGetTool("todos_delete", out tool));
            Assert.Equal("DELETE", tool!.HttpMethod);
        }

        [Fact]
        public void Unknown_tool_names_are_reported_as_missing()
        {
            using var scope = _fixture.Services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IMcpToolRegistry>();

            McpToolDescriptor? tool;
            Assert.False(registry.TryGetTool("does_not_exist", out tool));
            Assert.Null(tool);
        }

        [Fact]
        public void Tool_names_are_unique()
        {
            using var scope = _fixture.Services.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IMcpToolRegistry>();

            var names = registry.GetTools().Select(t => t.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }
    }
}
