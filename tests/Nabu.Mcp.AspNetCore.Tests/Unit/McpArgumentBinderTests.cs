using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Controllers;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;
using Nabu.Mcp.AspNetCore.Schema;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Unit
{
    public class McpArgumentBinderTests
    {
        public enum Priority
        {
            Low = 0,
            Normal = 1,
            High = 2,
        }

        public class Payload
        {
            public string Title { get; set; } = string.Empty;

            public Priority Priority { get; set; }

            public List<Child> Children { get; set; } = new List<Child>();
        }

        public class Child
        {
            public Priority Priority { get; set; }
        }

        private static readonly McpJsonCompatibility NumericEnums = new McpJsonCompatibility(stringEnumsEnabled: false);
        private static readonly McpJsonCompatibility StringEnums = new McpJsonCompatibility(stringEnumsEnabled: true);

        private static McpToolDescriptor Tool(
            string httpMethod,
            string routeTemplate,
            params McpToolParameterDescriptor[] parameters)
        {
            return new McpToolDescriptor(
                "test_tool",
                httpMethod,
                routeTemplate,
                new ControllerActionDescriptor(),
                parameters,
                new JsonObject(),
                new McpToolAnnotations());
        }

        private static McpToolParameterDescriptor Parameter(
            string name,
            McpParameterSource source,
            Type type,
            bool required = false,
            bool isBodyRoot = false,
            string? bindingName = null)
        {
            return new McpToolParameterDescriptor(name, bindingName ?? name, source, type, required, new JsonObject())
            {
                IsBodyRoot = isBodyRoot,
            };
        }

        [Fact]
        public void Route_arguments_are_substituted_into_the_path()
        {
            var tool = Tool("GET", "api/todos/{id}", Parameter("id", McpParameterSource.Route, typeof(Guid), required: true));
            var id = Guid.NewGuid();

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["id"] = id.ToString() }, NumericEnums);

            Assert.Equal("/api/todos/" + id, bound.Path);
            Assert.Equal(string.Empty, bound.QueryString);
            Assert.Null(bound.Body);
        }

        [Fact]
        public void Query_arguments_are_url_encoded()
        {
            var tool = Tool("GET", "api/todos", Parameter("search", McpParameterSource.Query, typeof(string)));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["search"] = "milk & eggs" }, NumericEnums);

            Assert.Equal("?search=milk%20%26%20eggs", bound.QueryString);
        }

        [Fact]
        public void Query_arrays_repeat_the_key()
        {
            var tool = Tool("GET", "api/todos", Parameter("tag", McpParameterSource.Query, typeof(string[])));
            var arguments = new JsonObject { ["tag"] = new JsonArray("a", "b") };

            var bound = McpArgumentBinder.Bind(tool, arguments, NumericEnums);

            Assert.Equal("?tag=a&tag=b", bound.QueryString);
        }

        [Fact]
        public void Query_objects_use_dotted_keys()
        {
            var tool = Tool("GET", "api/todos", Parameter("filter", McpParameterSource.Query, typeof(object)));
            var arguments = new JsonObject { ["filter"] = new JsonObject { ["from"] = 1, ["to"] = 2 } };

            var bound = McpArgumentBinder.Bind(tool, arguments, NumericEnums);

            Assert.Equal("?filter.from=1&filter.to=2", bound.QueryString);
        }

        [Fact]
        public void Booleans_and_numbers_are_rendered_without_quotes()
        {
            var tool = Tool(
                "GET",
                "api/todos",
                Parameter("done", McpParameterSource.Query, typeof(bool)),
                Parameter("page", McpParameterSource.Query, typeof(int)));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["done"] = true, ["page"] = 3 }, NumericEnums);

            Assert.Equal("?done=true&page=3", bound.QueryString);
        }

        [Fact]
        public void Flattened_body_properties_are_rebuilt_into_one_json_object()
        {
            var tool = Tool(
                "POST",
                "api/todos",
                Parameter("title", McpParameterSource.Body, typeof(string), required: true),
                Parameter("priority", McpParameterSource.Body, typeof(Priority)));

            var arguments = new JsonObject { ["title"] = "Write tests", ["priority"] = "High" };
            var bound = McpArgumentBinder.Bind(tool, arguments, StringEnums);

            var body = Assert.IsType<JsonObject>(bound.Body);
            Assert.Equal("Write tests", body["title"]!.GetValue<string>());
            Assert.Equal("High", body["priority"]!.GetValue<string>());
        }

        [Fact]
        public void Enum_names_become_numbers_when_the_app_does_not_use_string_enums()
        {
            var tool = Tool("POST", "api/todos", Parameter("priority", McpParameterSource.Body, typeof(Priority)));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["priority"] = "High" }, NumericEnums);

            Assert.Equal(2, bound.Body!["priority"]!.GetValue<int>());
        }

        [Fact]
        public void Enum_names_are_left_alone_when_the_app_uses_string_enums()
        {
            var tool = Tool("POST", "api/todos", Parameter("priority", McpParameterSource.Body, typeof(Priority)));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["priority"] = "High" }, StringEnums);

            Assert.Equal("High", bound.Body!["priority"]!.GetValue<string>());
        }

        [Fact]
        public void Enum_coercion_reaches_nested_objects_and_arrays()
        {
            var tool = Tool(
                "POST",
                "api/todos",
                Parameter("payload", McpParameterSource.Body, typeof(Payload), isBodyRoot: true));

            var arguments = new JsonObject
            {
                ["payload"] = new JsonObject
                {
                    ["title"] = "root",
                    ["priority"] = "Normal",
                    ["children"] = new JsonArray(new JsonObject { ["priority"] = "High" }),
                },
            };

            var bound = McpArgumentBinder.Bind(tool, arguments, NumericEnums);

            Assert.Equal(1, bound.Body!["priority"]!.GetValue<int>());
            Assert.Equal(2, bound.Body!["children"]!.AsArray()[0]!["priority"]!.GetValue<int>());
        }

        [Fact]
        public void Unknown_enum_names_are_passed_through_so_the_api_reports_the_error()
        {
            var tool = Tool("POST", "api/todos", Parameter("priority", McpParameterSource.Body, typeof(Priority)));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["priority"] = "Nonsense" }, NumericEnums);

            Assert.Equal("Nonsense", bound.Body!["priority"]!.GetValue<string>());
        }

        [Fact]
        public void A_body_root_parameter_becomes_the_whole_body()
        {
            var tool = Tool(
                "POST",
                "api/values",
                Parameter("values", McpParameterSource.Body, typeof(List<int>), isBodyRoot: true));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["values"] = new JsonArray(1, 2, 3) }, NumericEnums);

            Assert.Equal("[1,2,3]", bound.Body!.ToJsonString());
        }

        [Fact]
        public void Header_arguments_are_collected_separately()
        {
            var tool = Tool(
                "GET",
                "api/todos",
                Parameter("tenant", McpParameterSource.Header, typeof(string), bindingName: "X-Tenant"));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["tenant"] = "acme" }, NumericEnums);

            Assert.Equal("acme", bound.Headers["X-Tenant"]);
        }

        [Fact]
        public void Missing_required_arguments_are_rejected_before_the_request_is_built()
        {
            var tool = Tool("GET", "api/todos/{id}", Parameter("id", McpParameterSource.Route, typeof(Guid), required: true));

            var exception = Assert.Throws<McpArgumentException>(
                () => McpArgumentBinder.Bind(tool, new JsonObject(), NumericEnums));

            Assert.Contains("id", exception.Message);
        }

        [Fact]
        public void Omitted_optional_arguments_are_simply_left_out()
        {
            var tool = Tool(
                "GET",
                "api/todos",
                Parameter("search", McpParameterSource.Query, typeof(string)),
                Parameter("page", McpParameterSource.Query, typeof(int)));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["page"] = 2 }, NumericEnums);

            Assert.Equal("?page=2", bound.QueryString);
        }

        [Fact]
        public void Null_arguments_are_omitted_from_the_query_string()
        {
            var tool = Tool("GET", "api/todos", Parameter("search", McpParameterSource.Query, typeof(string)));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject { ["search"] = null }, NumericEnums);

            Assert.Equal(string.Empty, bound.QueryString);
        }

        [Fact]
        public void Null_arguments_are_accepted_for_optional_route_segments()
        {
            var tool = Tool("GET", "api/todos/{id?}", Parameter("id", McpParameterSource.Route, typeof(string)));

            var bound = McpArgumentBinder.Bind(tool, new JsonObject(), NumericEnums);

            Assert.Equal("/api/todos", bound.Path);
        }
    }
}
