using System;
using System.Collections.Generic;
using System.Linq;
using Nabu.Mcp.AspNetCore.Discovery;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Unit
{
    public class RouteTemplateHelperTests
    {
        [Theory]
        [InlineData("api/todos", "api/todos")]
        [InlineData("api/todos/{id}", "api/todos/{id}")]
        [InlineData("api/todos/{id:guid}", "api/todos/{id}")]
        [InlineData("api/todos/{id:int:min(1)}", "api/todos/{id}")]
        [InlineData("api/files/{*path}", "api/files/{path}")]
        [InlineData("/api/todos/{id:guid}", "api/todos/{id}")]
        [InlineData("api/{controller}/{action}/{id?}", "api/{controller}/{action}/{id}")]
        public void Normalize_strips_constraints_and_markers(string template, string expected)
        {
            Assert.Equal(expected, RouteTemplateHelper.Normalize(template));
        }

        [Fact]
        public void Normalize_does_not_escape_the_braces()
        {
            // Regression: escaping turned "{id}" into "%7Bid%7D", which silently disabled route binding.
            Assert.DoesNotContain("%7B", RouteTemplateHelper.Normalize("api/todos/{id:guid}"));
        }

        [Theory]
        [InlineData("api/todos/{id}", "id", true)]
        [InlineData("api/todos/{id}", "ID", true)]
        [InlineData("api/todos/{id}", "other", false)]
        [InlineData("api/todos", "id", false)]
        public void ContainsToken_matches_case_insensitively(string template, string name, bool expected)
        {
            Assert.Equal(expected, RouteTemplateHelper.ContainsToken(template, name));
        }

        [Fact]
        public void GetTokenNames_returns_every_token_in_order()
        {
            var names = RouteTemplateHelper.GetTokenNames("api/{tenant}/todos/{id:guid}/notes/{noteId?}");
            Assert.Equal(new[] { "tenant", "id", "noteId" }, names.ToArray());
        }

        [Fact]
        public void Substitute_fills_tokens_and_prefixes_a_slash()
        {
            var values = new Dictionary<string, string> { ["id"] = "42" };
            var path = RouteTemplateHelper.Substitute("api/todos/{id}", n => values.TryGetValue(n, out var v) ? v : null);

            Assert.Equal("/api/todos/42", path);
        }

        [Fact]
        public void Substitute_escapes_values()
        {
            var path = RouteTemplateHelper.Substitute("api/search/{term}", _ => "a b/c");
            Assert.Equal("/api/search/a%20b%2Fc", path);
        }

        [Fact]
        public void Substitute_keeps_separators_inside_a_catch_all_value()
        {
            var path = RouteTemplateHelper.Substitute("files/{*path}", _ => "a/b c.txt");
            Assert.Equal("/files/a/b%20c.txt", path);
        }

        [Fact]
        public void Substitute_drops_optional_tokens_that_have_no_value()
        {
            var path = RouteTemplateHelper.Substitute("api/todos/{id?}", _ => null);
            Assert.Equal("/api/todos", path);
        }

        [Fact]
        public void Substitute_drops_tokens_that_declare_a_default()
        {
            var path = RouteTemplateHelper.Substitute("api/{controller=Home}", _ => null);
            Assert.Equal("/api", path);
        }

        [Fact]
        public void Substitute_throws_when_a_required_token_has_no_value()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => RouteTemplateHelper.Substitute("api/todos/{id}", _ => null));

            Assert.Contains("id", exception.Message);
        }

        [Fact]
        public void Substitute_handles_escaped_braces()
        {
            var path = RouteTemplateHelper.Substitute("api/{{literal}}/{id}", _ => "7");
            Assert.Equal("/api/{literal}/7", path);
        }
    }
}
