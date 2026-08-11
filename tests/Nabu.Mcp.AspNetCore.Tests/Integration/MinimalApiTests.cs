using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nabu.Mcp.AspNetCore;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// Minimal API endpoints published with <c>.McpTool()</c> (or a <c>[McpTool]</c> attribute on the
    /// handler), in an application that registers no controllers at all.
    /// </summary>
    public class MinimalApiTests
    {
        /// <summary>A service the container can resolve, so handler parameters of this type bind from DI.</summary>
        public sealed class Multiplier
        {
            public int Apply(int value) => value * 2;
        }

        public sealed class CreateOrder
        {
            public string Title { get; set; } = string.Empty;

            public int Quantity { get; set; }
        }

        private static IHost CreateServer(
            Action<IEndpointRouteBuilder> map,
            Action<NabuMcpOptions>? configure = null,
            Action<IServiceCollection>? services = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(collection =>
                    {
                        collection.AddRouting();
                        collection.AddNabuMcp(configure ?? (_ => { }));
                        services?.Invoke(collection);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseNabuMcp();
                        app.UseEndpoints(endpoints => map(endpoints));
                    }))
                .Build();

            host.Start();
            return host;
        }

        private static async Task<JsonObject> RpcAsync(IHost server, string method, JsonObject? parameters = null)
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = method,
            };

            if (parameters != null)
            {
                request["params"] = parameters;
            }

            using var client = server.GetTestClient();
            using var response = await client.PostAsync(
                "/mcp",
                new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));

            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        private static async Task<JsonArray> ToolsAsync(IHost server)
        {
            var envelope = await RpcAsync(server, "tools/list");
            return envelope["result"]!["tools"]!.AsArray();
        }

        private static async Task<string[]> ToolNamesAsync(IHost server)
        {
            return (await ToolsAsync(server)).Select(t => t!["name"]!.GetValue<string>()).ToArray();
        }

        private static async Task<string> CallAsync(IHost server, string name, JsonObject arguments)
        {
            var envelope = await RpcAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = name, ["arguments"] = arguments });

            return envelope["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        }

        [Fact]
        public async Task McpTool_publishes_a_route_handler_with_a_route_derived_name()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapGet("/customers/{id:int}", (int id) => Results.Ok(new { id })).McpTool());

            var tools = await ToolsAsync(server);
            var tool = Assert.Single(tools, t => t!["name"]!.GetValue<string>() == "customers_get_by_id");

            var schema = tool!["inputSchema"]!.AsObject();
            Assert.Equal("integer", schema["properties"]!["id"]!["type"]!.GetValue<string>());
            Assert.Contains("id", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        }

        [Fact]
        public async Task Unadorned_endpoints_are_not_published()
        {
            using var server = CreateServer(endpoints =>
            {
                endpoints.MapGet("/visible", () => Results.Ok()).McpTool("visible_tool");
                endpoints.MapGet("/invisible", () => Results.Ok());
            });

            var names = await ToolNamesAsync(server);

            Assert.Contains("visible_tool", names);
            Assert.Single(names);
        }

        [Fact]
        public async Task An_explicit_name_and_description_are_honoured()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapGet("/customers/{id:int}", (int id) => Results.Ok(new { id }))
                         .McpTool("get_customer", "Fetches a single customer."));

            var tools = await ToolsAsync(server);
            var tool = Assert.Single(tools, t => t!["name"]!.GetValue<string>() == "get_customer");

            Assert.Equal("Fetches a single customer.", tool!["description"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_tool_call_replays_route_and_query_arguments_through_the_pipeline()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapGet("/greet/{name}", (string name, string? punctuation) =>
                    Results.Ok(new { greeting = "Hello, " + name + (punctuation ?? "!") })).McpTool("greet"));

            var text = await CallAsync(server, "greet", new JsonObject
            {
                ["name"] = "Ada",
                ["punctuation"] = "?!",
            });

            Assert.Equal("Hello, Ada?!", JsonNode.Parse(text)!["greeting"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_complex_parameter_becomes_a_flattened_json_body()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapPost("/orders", (CreateOrder order) =>
                    Results.Ok(new { order.Title, order.Quantity })).McpTool("orders_create"));

            var tools = await ToolsAsync(server);
            var tool = Assert.Single(tools, t => t!["name"]!.GetValue<string>() == "orders_create");
            var properties = tool!["inputSchema"]!["properties"]!.AsObject();

            // The body model's properties sit at the top level of the schema, not under "order".
            Assert.NotNull(properties["title"]);
            Assert.NotNull(properties["quantity"]);
            Assert.Null(properties["order"]);

            var text = await CallAsync(server, "orders_create", new JsonObject
            {
                ["title"] = "Widgets",
                ["quantity"] = 3,
            });

            var body = JsonNode.Parse(text)!;
            Assert.Equal("Widgets", body["title"]!.GetValue<string>());
            Assert.Equal(3, body["quantity"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_parameter_resolvable_from_the_container_is_not_a_tool_input()
        {
            using var server = CreateServer(
                endpoints => endpoints.MapGet("/double/{value:int}", (Multiplier multiplier, int value) =>
                    Results.Ok(new { result = multiplier.Apply(value) })).McpTool("double"),
                services: collection => collection.AddSingleton<Multiplier>());

            var tools = await ToolsAsync(server);
            var tool = Assert.Single(tools, t => t!["name"]!.GetValue<string>() == "double");
            var properties = tool!["inputSchema"]!["properties"]!.AsObject();

            Assert.Single(properties);
            Assert.NotNull(properties["value"]);

            var text = await CallAsync(server, "double", new JsonObject { ["value"] = 21 });
            Assert.Equal(42, JsonNode.Parse(text)!["result"]!.GetValue<int>());
        }

        [Fact]
        public async Task An_attribute_on_the_handler_lambda_works_like_the_extension()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapGet("/ping", [McpTool("ping_tool")] () => Results.Ok(new { pong = true })));

            Assert.Contains("ping_tool", await ToolNamesAsync(server));
        }

        [Fact]
        public async Task ExposeAllActions_publishes_route_handlers_and_McpIgnore_wins()
        {
            using var server = CreateServer(
                endpoints =>
                {
                    endpoints.MapGet("/list", () => Results.Ok());
                    endpoints.MapGet("/hidden", () => Results.Ok()).McpIgnore();
                },
                options => options.ExposeAllActions = true);

            var names = await ToolNamesAsync(server);

            Assert.Contains("list_get", names);
            Assert.DoesNotContain(names, n => n.Contains("hidden"));
        }

        [Fact]
        public async Task Controller_backed_endpoints_are_not_discovered_twice()
        {
            using var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(collection =>
                    {
                        collection
                            .AddControllers()
                            .AddApplicationPart(typeof(ProbeController).Assembly)
                            .OnlyControllers(typeof(ProbeController));

                        collection.AddNabuMcp(options => options.ExposeAllActions = true);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseNabuMcp();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapControllers();
                            endpoints.MapGet("/minimal", () => Results.Ok(new { minimal = true })).McpTool("minimal_tool");
                        });
                    }))
                .Build();

            host.Start();
            using var server = host;

            var names = await ToolNamesAsync(server);

            // Controller actions come from the MVC path only; the route handler from the endpoint path.
            Assert.Contains("probe_echo", names);
            Assert.Contains("minimal_tool", names);
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
            Assert.Single(names, n => n == "probe_echo");
        }

        [Fact]
        public async Task RequireAuthorization_hides_the_tool_from_anonymous_callers_when_visibility_is_authorized()
        {
            using var server = CreateServer(
                endpoints =>
                {
                    endpoints.MapGet("/open", () => Results.Ok()).McpTool("open_tool");
                    endpoints.MapGet("/secure", () => Results.Ok()).RequireAuthorization().McpTool("secure_tool");
                },
                options => options.ToolVisibility = McpToolVisibility.Authorized,
                services: collection => collection.AddAuthorization());

            var names = await ToolNamesAsync(server);

            Assert.Contains("open_tool", names);
            Assert.DoesNotContain("secure_tool", names);
        }

        [Fact]
        public async Task One_endpoint_can_publish_several_tools_with_pinned_constants()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapGet("/items", (bool? all, int? page) =>
                        Results.Ok(new { all = all ?? false, page = page ?? 0 }))
                    .McpTool("items_list")
                    .McpTool(tool =>
                    {
                        tool.Name = "items_list_all";
                        tool.ConstantParameters = new[] { "all=true" };
                    }));

            var tools = await ToolsAsync(server);
            var names = tools.Select(t => t!["name"]!.GetValue<string>()).ToArray();

            Assert.Contains("items_list", names);
            Assert.Contains("items_list_all", names);

            var pinned = tools.Single(t => t!["name"]!.GetValue<string>() == "items_list_all");
            Assert.Null(pinned!["inputSchema"]!["properties"]!["all"]);

            var text = await CallAsync(server, "items_list_all", new JsonObject { ["page"] = 2 });
            var body = JsonNode.Parse(text)!;
            Assert.True(body["all"]!.GetValue<bool>());
            Assert.Equal(2, body["page"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_non_nullable_value_type_without_a_default_is_required()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapGet("/page", (int page, int pageSize = 20) => Results.Ok(new { page, pageSize }))
                         .McpTool("page_tool"));

            var tools = await ToolsAsync(server);
            var tool = Assert.Single(tools, t => t!["name"]!.GetValue<string>() == "page_tool");
            var required = tool!["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

            // Minimal APIs answer 400 when `page` is missing, so the schema must demand it; `pageSize`
            // has a default and stays optional.
            Assert.Contains("page", required);
            Assert.DoesNotContain("pageSize", required);
        }
    }
}
