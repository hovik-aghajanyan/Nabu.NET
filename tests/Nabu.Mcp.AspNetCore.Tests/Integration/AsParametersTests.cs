using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nabu.Mcp.AspNetCore;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// Minimal API handlers that gather their inputs into an <c>[AsParameters]</c> surface type. The
    /// type's constructor parameters and settable properties are expanded into individual tool inputs
    /// with the same binding inference they get from <c>RequestDelegateFactory</c>.
    /// </summary>
    public class AsParametersTests
    {
        public record SearchRequest(string Category, string? Term, int Page = 1);

        public class PagingParams
        {
            [FromQuery(Name = "q")]
            public string? Query { get; set; }

            public int? PageSize { get; set; }
        }

        public sealed class Clock
        {
            public string Now => "now";
        }

        public class WithService
        {
            public string? Term { get; set; }

            [FromServices]
            public Clock Clock { get; set; } = null!;
        }

        private static IHost CreateServer(
            Action<IEndpointRouteBuilder> map,
            Action<IServiceCollection>? services = null)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(collection =>
                    {
                        collection.AddRouting();
                        collection.AddNabuMcp();
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

        private static async Task<JsonObject?> FindToolAsync(IHost server, string name)
        {
            var envelope = await RpcAsync(server, "tools/list");
            return envelope["result"]!["tools"]!.AsArray()
                .Select(t => t!.AsObject())
                .FirstOrDefault(t => t["name"]!.GetValue<string>() == name);
        }

        private static async Task<JsonNode> CallAsync(IHost server, string name, JsonObject arguments)
        {
            var envelope = await RpcAsync(
                server,
                "tools/call",
                new JsonObject { ["name"] = name, ["arguments"] = arguments });

            Assert.True(envelope["error"] == null, "tools/call returned an error: " + envelope["error"]?.ToJsonString());

            var result = envelope["result"]!.AsObject();
            Assert.False(result["isError"]?.GetValue<bool>() ?? false, "tool result was an error: " + result.ToJsonString());

            return JsonNode.Parse(result["content"]!.AsArray()[0]!["text"]!.GetValue<string>())!;
        }

        [Fact]
        public async Task A_record_is_expanded_into_route_and_query_inputs()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapGet("/search/{category}", ([AsParameters] SearchRequest request) =>
                        Results.Ok(new { request.Category, request.Term, request.Page }))
                    .McpTool("search"));

            var tool = await FindToolAsync(server, "search");
            Assert.NotNull(tool);

            var schema = tool!["inputSchema"]!.AsObject();
            var properties = schema["properties"]!.AsObject();

            // The surface type is gone: its members are individual inputs.
            Assert.Null(properties["request"]);
            Assert.NotNull(properties["category"]);
            Assert.NotNull(properties["term"]);
            Assert.NotNull(properties["page"]);

            var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Assert.Contains("category", required);      // route token
            Assert.DoesNotContain("term", required);    // nullable
            Assert.DoesNotContain("page", required);    // has a default value

            var body = await CallAsync(server, "search", new JsonObject
            {
                ["category"] = "books",
                ["term"] = "assyriology",
            });

            Assert.Equal("books", body["category"]!.GetValue<string>());
            Assert.Equal("assyriology", body["term"]!.GetValue<string>());
            Assert.Equal(1, body["page"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_property_based_class_is_expanded_and_FromQuery_names_are_honoured()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapGet("/items", ([AsParameters] PagingParams paging) =>
                        Results.Ok(new { paging.Query, paging.PageSize }))
                    .McpTool("items"));

            var tool = await FindToolAsync(server, "items");
            Assert.NotNull(tool);

            var properties = tool!["inputSchema"]!["properties"]!.AsObject();
            Assert.NotNull(properties["q"]);
            Assert.NotNull(properties["pageSize"]);

            var body = await CallAsync(server, "items", new JsonObject
            {
                ["q"] = "lamp",
                ["pageSize"] = 5,
            });

            Assert.Equal("lamp", body["query"]!.GetValue<string>());
            Assert.Equal(5, body["pageSize"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_FromServices_member_is_not_a_tool_input()
        {
            using var server = CreateServer(
                endpoints =>
                    endpoints.MapGet("/svc", ([AsParameters] WithService request) =>
                            Results.Ok(new { request.Term, now = request.Clock.Now }))
                        .McpTool("svc"),
                services: collection => collection.AddSingleton<Clock>());

            var tool = await FindToolAsync(server, "svc");
            Assert.NotNull(tool);

            var properties = tool!["inputSchema"]!["properties"]!.AsObject();
            Assert.NotNull(properties["term"]);
            Assert.Null(properties["clock"]);

            var body = await CallAsync(server, "svc", new JsonObject { ["term"] = "x" });

            Assert.Equal("x", body["term"]!.GetValue<string>());
            Assert.Equal("now", body["now"]!.GetValue<string>());
        }

        [Fact]
        public async Task An_AsParameters_type_can_carry_a_file()
        {
            using var server = CreateServer(endpoints =>
                endpoints.MapPost("/upload/{folder}", ([AsParameters] UploadRequest request) =>
                        Results.Ok(new { request.Folder, name = request.File.FileName }))
                    .DisableAntiforgery()
                    .McpTool("upload"));

            var tool = await FindToolAsync(server, "upload");
            Assert.NotNull(tool);

            var properties = tool!["inputSchema"]!["properties"]!.AsObject();
            Assert.NotNull(properties["folder"]);
            Assert.Equal("base64", properties["file"]!["properties"]!["data"]!["contentEncoding"]!.GetValue<string>());

            var body = await CallAsync(server, "upload", new JsonObject
            {
                ["folder"] = "docs",
                ["file"] = new JsonObject
                {
                    ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("payload")),
                    ["fileName"] = "notes.md",
                },
            });

            Assert.Equal("docs", body["folder"]!.GetValue<string>());
            Assert.Equal("notes.md", body["name"]!.GetValue<string>());
        }

        public class UploadRequest
        {
            public string Folder { get; set; } = string.Empty;

            public IFormFile File { get; set; } = null!;
        }
    }
}
