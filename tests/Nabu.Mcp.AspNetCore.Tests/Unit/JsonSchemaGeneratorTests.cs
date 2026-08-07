using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nabu.Mcp.AspNetCore;
using Nabu.Mcp.AspNetCore.Schema;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Unit
{
    public class JsonSchemaGeneratorTests
    {
        private readonly JsonSchemaGenerator _generator =
            new JsonSchemaGenerator(NullXmlDocumentationProvider.Instance, maxDepth: 4);

        public enum Colour
        {
            Red,
            Green,
        }

        public class Address
        {
            [Required]
            public string Street { get; set; } = string.Empty;

            public string? Region { get; set; }

            public int HouseNumber { get; set; }
        }

        public class Person
        {
            /// <summary>Ignored on purpose.</summary>
            [McpIgnore]
            public string Secret { get; set; } = string.Empty;

            [Description("Full legal name.")]
            [StringLength(50, MinimumLength = 2)]
            public string Name { get; set; } = string.Empty;

            [JsonPropertyName("years")]
            [Range(0, 130)]
            public int Age { get; set; }

            public Colour Favourite { get; set; }

            public Address? Home { get; set; }

            public List<string> Nicknames { get; set; } = new List<string>();

            public Dictionary<string, int> Scores { get; set; } = new Dictionary<string, int>();

            [JsonIgnore]
            public string Internal { get; set; } = string.Empty;
        }

        public class Node
        {
            public string Value { get; set; } = string.Empty;

            public Node? Next { get; set; }
        }

        [Theory]
        [InlineData(typeof(string), "string")]
        [InlineData(typeof(bool), "boolean")]
        [InlineData(typeof(int), "integer")]
        [InlineData(typeof(long), "integer")]
        [InlineData(typeof(double), "number")]
        [InlineData(typeof(decimal), "number")]
        public void Primitives_map_to_their_json_type(Type type, string expected)
        {
            Assert.Equal(expected, _generator.Generate(type)["type"]!.GetValue<string>());
        }

        [Theory]
        [InlineData(typeof(Guid), "uuid")]
        [InlineData(typeof(DateTime), "date-time")]
        [InlineData(typeof(DateTimeOffset), "date-time")]
        [InlineData(typeof(TimeSpan), "duration")]
        [InlineData(typeof(Uri), "uri")]
        [InlineData(typeof(DateOnly), "date")]
        public void Well_known_types_carry_a_format(Type type, string expected)
        {
            var schema = _generator.Generate(type);
            Assert.Equal("string", schema["type"]!.GetValue<string>());
            Assert.Equal(expected, schema["format"]!.GetValue<string>());
        }

        [Fact]
        public void Enums_are_described_by_name()
        {
            var schema = _generator.Generate(typeof(Colour));

            Assert.Equal("string", schema["type"]!.GetValue<string>());
            Assert.Equal(new[] { "Red", "Green" }, schema["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        }

        [Fact]
        public void Nullable_value_types_accept_null()
        {
            var schema = _generator.Generate(typeof(int?));
            var types = schema["type"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

            Assert.Equal(new[] { "integer", "null" }, types);
        }

        [Fact]
        public void Collections_become_arrays_with_item_schemas()
        {
            var schema = _generator.Generate(typeof(List<Colour>));

            Assert.Equal("array", schema["type"]!.GetValue<string>());
            Assert.Equal("string", schema["items"]!["type"]!.GetValue<string>());
        }

        [Fact]
        public void String_keyed_dictionaries_become_open_objects()
        {
            var schema = _generator.Generate(typeof(Dictionary<string, int>));

            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.Equal("integer", schema["additionalProperties"]!["type"]!.GetValue<string>());
        }

        [Fact]
        public void Complex_types_expose_their_properties()
        {
            var schema = _generator.Generate(typeof(Person));
            var properties = schema["properties"]!.AsObject();

            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.True(properties.ContainsKey("name"));
            Assert.True(properties.ContainsKey("home"));
            Assert.True(properties.ContainsKey("nicknames"));
        }

        [Fact]
        public void McpIgnore_and_JsonIgnore_remove_properties()
        {
            var properties = _generator.Generate(typeof(Person))["properties"]!.AsObject();

            Assert.False(properties.ContainsKey("secret"));
            Assert.False(properties.ContainsKey("internal"));
        }

        [Fact]
        public void JsonPropertyName_renames_the_schema_property()
        {
            var properties = _generator.Generate(typeof(Person))["properties"]!.AsObject();

            Assert.True(properties.ContainsKey("years"));
            Assert.False(properties.ContainsKey("age"));
        }

        [Fact]
        public void Validation_attributes_become_schema_constraints()
        {
            var properties = _generator.Generate(typeof(Person))["properties"]!.AsObject();

            var name = properties["name"]!;
            Assert.Equal("Full legal name.", name["description"]!.GetValue<string>());
            Assert.Equal(2, name["minLength"]!.GetValue<int>());
            Assert.Equal(50, name["maxLength"]!.GetValue<int>());

            var years = properties["years"]!;
            Assert.Equal(0, years["minimum"]!.GetValue<double>());
            Assert.Equal(130, years["maximum"]!.GetValue<double>());
        }

        [Fact]
        public void Required_is_driven_by_annotations_and_nullability()
        {
            var schema = _generator.Generate(typeof(Address));
            var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

            Assert.Contains("street", required);
            // Nullable reference type, so optional.
            Assert.DoesNotContain("region", required);
            // Non-nullable value types always have a value, so requiring them would be noise.
            Assert.DoesNotContain("houseNumber", required);
        }

        [Fact]
        public void Recursive_models_terminate_at_the_depth_limit()
        {
            var schema = _generator.Generate(typeof(Node));

            // The generator must not blow the stack, and the result must stay a valid object schema.
            Assert.Equal("object", schema["type"]!.GetValue<string>());
            Assert.True(schema["properties"]!.AsObject().ContainsKey("next"));
        }

        [Fact]
        public void TryExpandObject_lifts_properties_with_their_clr_types()
        {
            IList<JsonSchemaGenerator.ExpandedProperty> properties;
            Assert.True(_generator.TryExpandObject(typeof(Address), out properties));

            var street = properties.Single(p => p.Name == "street");
            Assert.Equal(typeof(string), street.ClrType);
            Assert.True(street.IsRequired);

            var houseNumber = properties.Single(p => p.Name == "houseNumber");
            Assert.Equal(typeof(int), houseNumber.ClrType);
        }

        [Fact]
        public void TryExpandObject_rejects_non_object_types()
        {
            IList<JsonSchemaGenerator.ExpandedProperty> properties;

            Assert.False(_generator.TryExpandObject(typeof(string), out properties));
            Assert.False(_generator.TryExpandObject(typeof(int), out properties));
            Assert.False(_generator.TryExpandObject(typeof(List<int>), out properties));
        }

        [Fact]
        public void Attribute_supplied_descriptions_reach_the_schema()
        {
            var attributes = new Attribute[] { new McpParameterAttribute("How many rows to return.") { Example = 25 } };
            var schema = _generator.Generate(typeof(int), attributes);

            Assert.Equal("How many rows to return.", schema["description"]!.GetValue<string>());
            Assert.Equal(25, schema["examples"]!.AsArray()[0]!.GetValue<int>());
        }

        [Fact]
        public void Enum_defaults_are_rendered_as_names_not_numbers()
        {
            var attributes = new Attribute[] { new DefaultValueAttribute(Colour.Green) };
            var schema = _generator.Generate(typeof(Colour), attributes);

            Assert.Equal("Green", schema["default"]!.GetValue<string>());
        }
    }
}
