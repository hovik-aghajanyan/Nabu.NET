using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// The sample application's showcase of the form-upload and <c>[AsParameters]</c> features:
    /// <c>todos_attach</c> (an <see cref="Microsoft.AspNetCore.Http.IFormFile"/> action on the
    /// authorized controller) and <c>server_time_in_zone</c> (a Minimal API handler binding an
    /// <c>[AsParameters]</c> record).
    /// </summary>
    [Collection(McpTestCollection.Name)]
    public class SampleUploadAndAsParametersTests
    {
        private readonly McpTestFixture _fixture;

        public SampleUploadAndAsParametersTests(McpTestFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Todos_attach_is_advertised_with_a_base64_file_argument()
        {
            var token = await _fixture.GetTokenAsync("alice");
            var envelope = await _fixture.RpcAsync("tools/list", token: token);
            var tools = envelope["result"]!["tools"]!.AsArray();

            var attach = Assert.Single(tools, t => t!["name"]!.GetValue<string>() == "todos_attach")!.AsObject();
            var properties = attach["inputSchema"]!["properties"]!.AsObject();

            Assert.Equal("base64", properties["file"]!["properties"]!["data"]!["contentEncoding"]!.GetValue<string>());
            Assert.NotNull(properties["description"]);

            var required = attach["inputSchema"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
            Assert.Contains("id", required);
            Assert.Contains("file", required);
            Assert.DoesNotContain("description", required);
        }

        [Fact]
        public async Task Todos_attach_uploads_a_file_to_the_signed_in_users_item()
        {
            var token = await _fixture.GetTokenAsync("alice");

            var created = McpTestFixture.JsonOf(await _fixture.CallToolAsync(
                "todos_create",
                new JsonObject { ["title"] = "Review the audit report" },
                token)).AsObject();

            var result = await _fixture.CallToolAsync(
                "todos_attach",
                new JsonObject
                {
                    ["id"] = created["id"]!.GetValue<string>(),
                    ["description"] = "The report to review",
                    ["file"] = new JsonObject
                    {
                        ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("audit findings")),
                        ["fileName"] = "audit.txt",
                        ["contentType"] = "text/plain",
                    },
                },
                token);

            Assert.False(McpTestFixture.IsError(result));

            var attachment = McpTestFixture.JsonOf(result).AsObject();
            Assert.Equal("audit.txt", attachment["fileName"]!.GetValue<string>());
            Assert.Equal("text/plain", attachment["contentType"]!.GetValue<string>());
            Assert.Equal("audit findings".Length, attachment["sizeBytes"]!.GetValue<long>());
            Assert.Equal("The report to review", attachment["description"]!.GetValue<string>());

            // The attachment is visible on the item afterwards, proving the action really ran.
            var item = McpTestFixture.JsonOf(await _fixture.CallToolAsync(
                "todos_get_by_id",
                new JsonObject { ["id"] = created["id"]!.GetValue<string>() },
                token)).AsObject();

            Assert.Single(item["attachments"]!.AsArray());
        }

        [Fact]
        public async Task Server_time_in_zone_expands_the_AsParameters_record()
        {
            var envelope = await _fixture.RpcAsync("tools/list");
            var tools = envelope["result"]!["tools"]!.AsArray();

            var tool = Assert.Single(tools, t => t!["name"]!.GetValue<string>() == "server_time_in_zone")!.AsObject();
            var schema = tool["inputSchema"]!.AsObject();
            var properties = schema["properties"]!.AsObject();

            // The record's members are individual inputs; the record itself never appears.
            Assert.NotNull(properties["timeZone"]);
            Assert.NotNull(properties["format"]);
            Assert.Null(properties["query"]);
            Assert.Contains("timeZone", schema["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        }

        [Fact]
        public async Task Server_time_in_zone_is_callable_anonymously()
        {
            var result = await _fixture.CallToolAsync(
                "server_time_in_zone",
                new JsonObject { ["timeZone"] = "UTC", ["format"] = "yyyy-MM-dd" });

            Assert.False(McpTestFixture.IsError(result));

            var payload = McpTestFixture.JsonOf(result).AsObject();
            Assert.Equal("UTC", payload["timeZone"]!.GetValue<string>());
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", payload["now"]!.GetValue<string>());
        }

        [Fact]
        public async Task An_unknown_time_zone_is_a_tool_error_the_model_can_read()
        {
            var result = await _fixture.CallToolAsync(
                "server_time_in_zone",
                new JsonObject { ["timeZone"] = "Mars/Olympus_Mons" });

            Assert.True(McpTestFixture.IsError(result));
            Assert.Contains("Unknown time zone", McpTestFixture.TextOf(result));
        }
    }
}
