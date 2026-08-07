using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.Discovery;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Unit
{
    public class ToolNamingTests
    {
        [Theory]
        [InlineData("Todos", "todos")]
        [InlineData("GetById", "get_by_id")]
        [InlineData("HTTPResponse", "http_response")]
        [InlineData("Get2Items", "get2_items")]
        [InlineData("alreadySnake", "already_snake")]
        [InlineData("", "")]
        public void ToSnakeCase_produces_readable_identifiers(string input, string expected)
        {
            Assert.Equal(expected, NabuMcpOptions.ToSnakeCase(input));
        }

        [Fact]
        public void Default_tool_name_joins_controller_and_action()
        {
            var context = new McpToolNamingContext("Todos", "GetById", "GET", "api/todos/{id}");
            Assert.Equal("todos_get_by_id", NabuMcpOptions.DefaultToolName(context));
        }

        [Fact]
        public void Default_tool_name_falls_back_to_the_action_when_there_is_no_controller()
        {
            var context = new McpToolNamingContext(string.Empty, "Ping", "GET", "ping");
            Assert.Equal("ping", NabuMcpOptions.DefaultToolName(context));
        }

        [Theory]
        [InlineData("todos.list", "todos_list")]
        [InlineData("todos/list", "todos_list")]
        [InlineData("__weird__", "weird")]
        [InlineData("keep-dashes", "keep-dashes")]
        public void Sanitize_keeps_only_characters_that_are_valid_in_a_tool_name(string input, string expected)
        {
            Assert.Equal(expected, McpToolRegistry.Sanitize(input));
        }

        [Theory]
        [InlineData("GetById", "Get By Id")]
        [InlineData("List", "List")]
        public void Humanize_produces_a_display_title(string input, string expected)
        {
            Assert.Equal(expected, McpToolRegistry.Humanize(input));
        }
    }
}
