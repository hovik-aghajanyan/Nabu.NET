using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Schema;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Unit
{
    public class ToolSourceTests
    {
        private sealed class FakeToolSource : IMcpToolSource
        {
            private readonly IReadOnlyList<McpToolDescriptor> _tools;

            public FakeToolSource(params McpToolDescriptor[] tools)
            {
                _tools = tools;
            }

            public IReadOnlyList<McpToolDescriptor> GetTools()
            {
                return _tools;
            }
        }

        private static McpToolDescriptor Tool(string name)
        {
            return new McpToolDescriptor(
                name,
                new McpToolParameterDescriptor[0],
                new JsonObject { ["type"] = "object" },
                new McpToolAnnotations { Title = name });
        }

        private static McpToolRegistry CreateRegistry(NabuMcpOptions options, params IMcpToolSource[] sources)
        {
            return new McpToolRegistry(
                actionProvider: null,
                Options.Create(options),
                new JsonSchemaGenerator(NullXmlDocumentationProvider.Instance),
                NullXmlDocumentationProvider.Instance,
                endpointDataSource: null,
                serviceProviderIsService: null,
                logger: null,
                toolSources: sources);
        }

        [Fact]
        public void Source_contributed_tools_are_listed_and_resolvable_by_name()
        {
            var registry = CreateRegistry(new NabuMcpOptions(), new FakeToolSource(Tool("alpha_tool")));

            var tools = registry.GetTools();

            Assert.Contains(tools, tool => tool.Name == "alpha_tool");
            Assert.True(registry.TryGetTool("alpha_tool", out var resolved));
            Assert.Equal("alpha_tool", resolved!.Name);
        }

        [Fact]
        public void Source_tools_are_sorted_into_the_catalogue()
        {
            var registry = CreateRegistry(
                new NabuMcpOptions(),
                new FakeToolSource(Tool("zeta_tool"), Tool("alpha_tool")));

            var tools = registry.GetTools();

            Assert.Equal(2, tools.Count);
            Assert.Equal("alpha_tool", tools[0].Name);
            Assert.Equal("zeta_tool", tools[1].Name);
        }

        [Fact]
        public void A_source_tool_whose_name_is_already_taken_is_skipped()
        {
            var registry = CreateRegistry(
                new NabuMcpOptions(),
                new FakeToolSource(Tool("dup_tool")),
                new FakeToolSource(Tool("dup_tool")));

            var tools = registry.GetTools();

            Assert.Single(tools);
        }

        [Fact]
        public void Tool_filter_applies_to_source_tools()
        {
            var options = new NabuMcpOptions
            {
                ToolFilter = tool => tool.Name != "dropped_tool",
            };

            var registry = CreateRegistry(
                options,
                new FakeToolSource(Tool("kept_tool"), Tool("dropped_tool")));

            var tools = registry.GetTools();

            Assert.Single(tools);
            Assert.Equal("kept_tool", tools[0].Name);
        }

        [Fact]
        public void Source_agnostic_descriptor_reports_no_http_shape()
        {
            var tool = Tool("plain_tool");

            Assert.Equal(string.Empty, tool.HttpMethod);
            Assert.Equal(string.Empty, tool.RouteTemplate);
            Assert.Null(tool.InvokerType);
            Assert.Equal("plain_tool", tool.ToString());
        }
    }
}
