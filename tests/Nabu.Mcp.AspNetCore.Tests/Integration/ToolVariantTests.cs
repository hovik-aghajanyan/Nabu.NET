using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// Covers publishing one controller action several times, each occurrence of <c>[McpTool]</c>
    /// narrowing the parameter set differently.
    /// </summary>
    [Collection(McpTestCollection.Name)]
    public class ToolVariantTests
    {
        private readonly McpTestFixture _fixture;

        public ToolVariantTests(McpTestFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<JsonArray> ListToolsAsync()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var envelope = await _fixture.RpcAsync("tools/list", token: token);
            return envelope["result"]!["tools"]!.AsArray();
        }

        private static JsonObject Find(JsonArray tools, string name)
        {
            var tool = tools.FirstOrDefault(t => t!["name"]!.GetValue<string>() == name);
            Assert.NotNull(tool);
            return tool!.AsObject();
        }

        private static string[] PropertyNames(JsonObject tool)
        {
            return tool["inputSchema"]!["properties"]!.AsObject().Select(p => p.Key).ToArray();
        }

        private static string[] RequiredNames(JsonObject tool)
        {
            var required = tool["inputSchema"]!["required"];
            return required == null
                ? new string[0]
                : required.AsArray().Select(r => r!.GetValue<string>()).ToArray();
        }

        [Fact]
        public async Task Every_McpTool_attribute_on_an_action_publishes_its_own_tool()
        {
            var tools = await ListToolsAsync();
            var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();

            // All three come from TodosController.List.
            Assert.Contains("todos_list", names);
            Assert.Contains("todos_list_open", names);
            Assert.Contains("todos_search", names);
        }

        [Fact]
        public async Task Variants_keep_their_own_title_and_description()
        {
            var tools = await ListToolsAsync();

            var open = Find(tools, "todos_list_open");
            Assert.Equal("List open todos", open["annotations"]!["title"]!.GetValue<string>());
            Assert.Contains("still open", open["description"]!.GetValue<string>());

            var search = Find(tools, "todos_search");
            Assert.Equal("Search todos", search["annotations"]!["title"]!.GetValue<string>());
        }

        [Fact]
        public async Task Excluded_and_pinned_parameters_disappear_from_the_schema()
        {
            var tools = await ListToolsAsync();

            var all = PropertyNames(Find(tools, "todos_list"));
            Assert.Contains("isCompleted", all);
            Assert.Contains("priority", all);
            Assert.Contains("search", all);

            var open = PropertyNames(Find(tools, "todos_list_open"));
            Assert.DoesNotContain("isCompleted", open); // pinned
            Assert.DoesNotContain("priority", open);    // excluded
            Assert.DoesNotContain("search", open);      // excluded
            Assert.Contains("page", open);
            Assert.Contains("pageSize", open);
        }

        [Fact]
        public async Task IncludeParameters_keeps_only_the_listed_inputs()
        {
            var tools = await ListToolsAsync();
            var search = PropertyNames(Find(tools, "todos_search"));

            Assert.Equal(new[] { "search", "page", "pageSize" }, search);
        }

        [Fact]
        public async Task RequiredParameters_promotes_an_optional_input()
        {
            var tools = await ListToolsAsync();

            Assert.Empty(RequiredNames(Find(tools, "todos_list")));
            Assert.Equal(new[] { "search" }, RequiredNames(Find(tools, "todos_search")));
        }

        [Fact]
        public async Task A_pinned_value_is_applied_to_the_underlying_request()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var created = McpTestFixture.JsonOf(await _fixture.CallToolAsync(
                "todos_create",
                new JsonObject { ["title"] = "Pinned argument probe" },
                token));
            var id = created["id"]!.GetValue<string>();

            var ids = await OpenIdsAsync(token);
            Assert.Contains(id, ids);

            await _fixture.CallToolAsync(
                "todos_update",
                new JsonObject { ["id"] = id, ["isCompleted"] = true },
                token);

            // The pinned isCompleted=false is the only thing that can drop it from the list; the plain
            // todos_list tool, which applies no filter, must still return it.
            Assert.DoesNotContain(id, await OpenIdsAsync(token));

            var all = McpTestFixture.JsonOf(
                await _fixture.CallToolAsync("todos_list", new JsonObject { ["pageSize"] = 100 }, token));
            Assert.Contains(id, all["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()));
        }

        private async Task<string[]> OpenIdsAsync(System.Net.Http.Headers.AuthenticationHeaderValue token)
        {
            var open = McpTestFixture.JsonOf(
                await _fixture.CallToolAsync("todos_list_open", new JsonObject { ["pageSize"] = 100 }, token));

            Assert.All(
                open["items"]!.AsArray(),
                item => Assert.False(item!["isCompleted"]!.GetValue<bool>()));

            return open["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToArray();
        }

        [Fact]
        public async Task A_pinned_value_overrides_an_argument_that_targets_the_same_input()
        {
            var token = await _fixture.GetTokenAsync("alice");

            // "isCompleted" is not part of todos_list_open's schema; supplying it anyway must not win
            // over the pinned value.
            var result = McpTestFixture.JsonOf(await _fixture.CallToolAsync(
                "todos_list_open",
                new JsonObject { ["isCompleted"] = true, ["pageSize"] = 100 },
                token));

            Assert.All(
                result["items"]!.AsArray(),
                item => Assert.False(item!["isCompleted"]!.GetValue<bool>()));
        }

        [Fact]
        public async Task A_route_token_can_be_pinned_leaving_a_tool_with_no_arguments()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var tools = await ListToolsAsync();
            var tool = Find(tools, "weather_get_yerevan_week");

            Assert.Empty(PropertyNames(tool));

            var forecast = McpTestFixture.JsonOf(
                await _fixture.CallToolAsync("weather_get_yerevan_week", new JsonObject(), token));

            Assert.Equal(7, forecast.AsArray().Count);

            // Same city, so the deterministic forecast must match the general-purpose tool.
            var direct = McpTestFixture.JsonOf(await _fixture.CallToolAsync(
                "weather_get_forecast",
                new JsonObject { ["city"] = "Yerevan", ["days"] = 7 },
                token));

            Assert.Equal(direct.ToJsonString(), forecast.ToJsonString());
        }
    }
}
