using System;
using System.IO;
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
    /// <summary>Controllers used only by <see cref="FormBindingTests"/>.</summary>
    [ApiController]
    [Route("api/files")]
    public class FormUploadController : ControllerBase
    {
        [HttpPost("upload")]
        [McpTool("files_upload")]
        public IActionResult Upload([FromForm] string title, IFormFile file)
        {
            using var reader = new StreamReader(file.OpenReadStream());
            return Ok(new
            {
                title,
                file.FileName,
                file.ContentType,
                content = reader.ReadToEnd(),
            });
        }

        [HttpPost("product")]
        [McpTool("files_product")]
        public IActionResult Product([FromForm] ProductForm form)
        {
            return Ok(new
            {
                form.Name,
                form.Quantity,
                imageName = form.Image?.FileName,
            });
        }

        public class ProductForm
        {
            public string Name { get; set; } = string.Empty;

            public int Quantity { get; set; }

            public IFormFile? Image { get; set; }
        }
    }

    /// <summary>
    /// Endpoints that bind form data - <see cref="IFormFile"/>, <c>[FromForm]</c> fields and models,
    /// <see cref="IFormCollection"/> - published as tools. The caller supplies file content as base64;
    /// the invoker replays it as a real multipart/form-data (or urlencoded) request, so the action's
    /// own form model binding does the rest.
    /// </summary>
    public class FormBindingTests
    {
        private static IHost CreateMinimalServer(Action<IEndpointRouteBuilder> map)
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(collection =>
                    {
                        collection.AddRouting();
                        collection.AddNabuMcp();
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

        private static IHost CreateControllerServer()
        {
            var host = new HostBuilder()
                .ConfigureWebHost(webHost => webHost
                    .UseTestServer()
                    .ConfigureServices(collection =>
                    {
                        collection
                            .AddControllers()
                            .AddApplicationPart(typeof(FormUploadController).Assembly)
                            .OnlyControllers(typeof(FormUploadController));

                        collection.AddNabuMcp();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseNabuMcp();
                        app.UseEndpoints(endpoints => endpoints.MapControllers());
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

        private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

        [Fact]
        public async Task An_IFormFile_parameter_is_advertised_as_a_base64_file_argument()
        {
            using var server = CreateMinimalServer(endpoints =>
                endpoints.MapPost("/upload", (IFormFile file) => Results.Ok(new { file.FileName }))
                         .DisableAntiforgery()
                         .McpTool("upload_file"));

            var tool = await FindToolAsync(server, "upload_file");
            Assert.NotNull(tool);

            var schema = tool!["inputSchema"]!.AsObject();
            var file = schema["properties"]!["file"]!.AsObject();

            Assert.Equal("object", file["type"]!.GetValue<string>());
            Assert.Equal("base64", file["properties"]!["data"]!["contentEncoding"]!.GetValue<string>());
            Assert.Contains("data", file["required"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.Contains("file", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        }

        [Fact]
        public async Task A_minimal_api_file_upload_round_trips_through_the_pipeline()
        {
            using var server = CreateMinimalServer(endpoints =>
                endpoints.MapPost("/upload", async (IFormFile file, [FromForm] string title) =>
                    {
                        using var reader = new StreamReader(file.OpenReadStream());
                        return Results.Ok(new
                        {
                            title,
                            file.FileName,
                            file.ContentType,
                            content = await reader.ReadToEndAsync(),
                        });
                    })
                    .DisableAntiforgery()
                    .McpTool("upload_file"));

            var body = await CallAsync(server, "upload_file", new JsonObject
            {
                ["title"] = "Quarterly report",
                ["file"] = new JsonObject
                {
                    ["data"] = Base64("hello, nabu"),
                    ["fileName"] = "report.txt",
                    ["contentType"] = "text/plain",
                },
            });

            Assert.Equal("Quarterly report", body["title"]!.GetValue<string>());
            Assert.Equal("report.txt", body["fileName"]!.GetValue<string>());
            Assert.Equal("text/plain", body["contentType"]!.GetValue<string>());
            Assert.Equal("hello, nabu", body["content"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_bare_base64_string_is_accepted_and_defaults_are_applied()
        {
            using var server = CreateMinimalServer(endpoints =>
                endpoints.MapPost("/upload", (IFormFile file) =>
                        Results.Ok(new { file.FileName, file.ContentType, size = file.Length }))
                    .DisableAntiforgery()
                    .McpTool("upload_file"));

            var body = await CallAsync(server, "upload_file", new JsonObject
            {
                ["file"] = Base64("abc"),
            });

            Assert.Equal("file", body["fileName"]!.GetValue<string>());
            Assert.Equal("application/octet-stream", body["contentType"]!.GetValue<string>());
            Assert.Equal(3, body["size"]!.GetValue<long>());
        }

        [Fact]
        public async Task A_collection_of_files_becomes_an_array_argument()
        {
            using var server = CreateMinimalServer(endpoints =>
                endpoints.MapPost("/upload-many", (IFormFileCollection files) =>
                        Results.Ok(new { names = files.Select(f => f.FileName).ToArray() }))
                    .DisableAntiforgery()
                    .McpTool("upload_many"));

            var tool = await FindToolAsync(server, "upload_many");
            Assert.Equal("array", tool!["inputSchema"]!["properties"]!["files"]!["type"]!.GetValue<string>());

            var body = await CallAsync(server, "upload_many", new JsonObject
            {
                ["files"] = new JsonArray(
                    new JsonObject { ["data"] = Base64("one"), ["fileName"] = "a.txt" },
                    new JsonObject { ["data"] = Base64("two"), ["fileName"] = "b.txt" }),
            });

            Assert.Equal(new[] { "a.txt", "b.txt" }, body["names"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        }

        [Fact]
        public async Task Form_fields_without_files_are_sent_urlencoded()
        {
            using var server = CreateMinimalServer(endpoints =>
                endpoints.MapPost("/register", ([FromForm] string name, [FromForm] int age) =>
                        Results.Ok(new { name, age }))
                    .DisableAntiforgery()
                    .McpTool("register"));

            var body = await CallAsync(server, "register", new JsonObject
            {
                ["name"] = "Ada",
                ["age"] = 36,
            });

            Assert.Equal("Ada", body["name"]!.GetValue<string>());
            Assert.Equal(36, body["age"]!.GetValue<int>());
        }

        [Fact]
        public async Task Invalid_base64_is_an_invalid_params_error_not_a_crash()
        {
            using var server = CreateMinimalServer(endpoints =>
                endpoints.MapPost("/upload", (IFormFile file) => Results.Ok())
                    .DisableAntiforgery()
                    .McpTool("upload_file"));

            var envelope = await RpcAsync(server, "tools/call", new JsonObject
            {
                ["name"] = "upload_file",
                ["arguments"] = new JsonObject
                {
                    ["file"] = new JsonObject { ["data"] = "not base64 at all!" },
                },
            });

            Assert.Equal(-32602, envelope["error"]!["code"]!.GetValue<int>());
        }

        [Fact]
        public async Task A_controller_action_with_FromForm_and_IFormFile_is_published_and_invokable()
        {
            using var server = CreateControllerServer();

            var tool = await FindToolAsync(server, "files_upload");
            Assert.NotNull(tool);

            var properties = tool!["inputSchema"]!["properties"]!.AsObject();
            Assert.NotNull(properties["title"]);
            Assert.NotNull(properties["file"]);

            var body = await CallAsync(server, "files_upload", new JsonObject
            {
                ["title"] = "Invoice",
                ["file"] = new JsonObject
                {
                    ["data"] = Base64("controller upload"),
                    ["fileName"] = "invoice.pdf",
                    ["contentType"] = "application/pdf",
                },
            });

            Assert.Equal("Invoice", body["title"]!.GetValue<string>());
            Assert.Equal("invoice.pdf", body["fileName"]!.GetValue<string>());
            Assert.Equal("controller upload", body["content"]!.GetValue<string>());
        }

        [Fact]
        public async Task A_FromForm_model_is_flattened_and_its_file_property_stays_a_file_argument()
        {
            using var server = CreateControllerServer();

            var tool = await FindToolAsync(server, "files_product");
            Assert.NotNull(tool);

            var properties = tool!["inputSchema"]!["properties"]!.AsObject();

            // The model's scalar properties are lifted to the top level; the wrapper is gone.
            Assert.NotNull(properties["name"]);
            Assert.NotNull(properties["quantity"]);
            Assert.Null(properties["form"]);

            // The file-typed property is advertised as a file argument, not as a nested object dump.
            Assert.Equal("base64", properties["image"]!["properties"]!["data"]!["contentEncoding"]!.GetValue<string>());

            var body = await CallAsync(server, "files_product", new JsonObject
            {
                ["name"] = "Widget",
                ["quantity"] = 7,
                ["image"] = new JsonObject { ["data"] = Base64("png-ish"), ["fileName"] = "widget.png" },
            });

            Assert.Equal("Widget", body["name"]!.GetValue<string>());
            Assert.Equal(7, body["quantity"]!.GetValue<int>());
            Assert.Equal("widget.png", body["imageName"]!.GetValue<string>());
        }

        [Fact]
        public async Task An_optional_file_can_be_omitted()
        {
            using var server = CreateControllerServer();

            var body = await CallAsync(server, "files_product", new JsonObject
            {
                ["name"] = "Widget",
                ["quantity"] = 1,
            });

            Assert.Equal("Widget", body["name"]!.GetValue<string>());
            Assert.True(body["imageName"] == null || body["imageName"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null);
        }

        [Fact]
        public async Task An_IFormCollection_parameter_accepts_a_free_form_object()
        {
            using var server = CreateMinimalServer(endpoints =>
                endpoints.MapPost("/fields", (IFormCollection form) =>
                        Results.Ok(new { color = form["color"].ToString(), size = form["size"].ToString() }))
                    .DisableAntiforgery()
                    .McpTool("fields"));

            var body = await CallAsync(server, "fields", new JsonObject
            {
                ["form"] = new JsonObject { ["color"] = "red", ["size"] = 42 },
            });

            Assert.Equal("red", body["color"]!.GetValue<string>());
            Assert.Equal("42", body["size"]!.GetValue<string>());
        }
    }
}
