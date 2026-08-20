using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;
using Nabu.Mcp.AspNetCore.Schema;
using Nabu.Mcp.AspNetCore.SignalR.Discovery;
using Nabu.Mcp.AspNetCore.SignalR.Execution;
using Xunit;

namespace Nabu.Mcp.AspNetCore.SignalR.Tests.Unit
{
    public class HubArgumentBinderTests
    {
        public enum Priority
        {
            Low = 0,
            High = 2,
        }

        public class BinderHub : Hub
        {
            [McpTool]
            public Task Full(string text, int count = 5, string? note = null) => Task.CompletedTask;

            [McpTool]
            public Task Enums(Priority priority) => Task.CompletedTask;

            [McpTool("pinned", ConstantParameters = new[] { "count=99" })]
            public Task Pinned(string text, int count) => Task.CompletedTask;
        }

        private static (McpToolDescriptor Tool, SignalRHubToolMetadata Metadata) Discover(string name)
        {
            var source = new SignalRHubToolSource(
                endpointDataSource: null,
                Options.Create(new NabuMcpOptions()),
                Options.Create(new NabuMcpSignalROptions()),
                new JsonSchemaGenerator(NullXmlDocumentationProvider.Instance),
                NullXmlDocumentationProvider.Instance);

            var tools = source.CreateHubTools(typeof(BinderHub), "/hubs/binder", new HashSet<string>(StringComparer.Ordinal));
            var tool = tools.Single(t => t.Name == name);
            Assert.True(SignalRHubToolSource.TryGetMetadata(tool, out var metadata));
            return (tool, metadata!);
        }

        private static JsonNode?[] Bind(string toolName, JsonObject? arguments, bool stringEnums = false)
        {
            var (tool, metadata) = Discover(toolName);
            return SignalRHubArgumentBinder.Bind(tool, metadata, arguments, new McpJsonCompatibility(stringEnums));
        }

        [Fact]
        public void Binds_positionally_in_declaration_order()
        {
            var args = Bind("binder_full", new JsonObject { ["text"] = "hi", ["count"] = 3, ["note"] = "n" });

            Assert.Equal(3, args.Length);
            Assert.Equal("hi", args[0]!.GetValue<string>());
            Assert.Equal(3, args[1]!.GetValue<int>());
            Assert.Equal("n", args[2]!.GetValue<string>());
        }

        [Fact]
        public void Missing_optional_arguments_fall_back_to_defaults()
        {
            var args = Bind("binder_full", new JsonObject { ["text"] = "hi" });

            Assert.Equal(5, args[1]!.GetValue<int>());
            Assert.Null(args[2]);
        }

        [Fact]
        public void Missing_required_argument_is_reported_as_an_argument_error()
        {
            var ex = Assert.Throws<McpArgumentException>(() => Bind("binder_full", new JsonObject()));
            Assert.Contains("text", ex.Message);
        }

        [Fact]
        public void Enum_names_are_coerced_to_numbers_for_a_numeric_serializer()
        {
            var args = Bind("binder_enums", new JsonObject { ["priority"] = "High" });

            Assert.Equal(2, args[0]!.GetValue<int>());
        }

        [Fact]
        public void Enum_names_stay_names_for_a_string_enum_serializer()
        {
            var args = Bind("binder_enums", new JsonObject { ["priority"] = "High" }, stringEnums: true);

            Assert.Equal("High", args[0]!.GetValue<string>());
        }

        [Fact]
        public void Constants_are_always_written_and_win_over_caller_arguments()
        {
            var args = Bind("pinned", new JsonObject { ["text"] = "hi", ["count"] = 1 });

            Assert.Equal("hi", args[0]!.GetValue<string>());
            Assert.Equal(99, args[1]!.GetValue<long>());
        }
    }
}
