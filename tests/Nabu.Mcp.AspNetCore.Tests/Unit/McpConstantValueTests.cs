using System;
using System.Collections.Generic;
using System.Text.Json;
using Nabu.Mcp.AspNetCore.Discovery;
using Xunit;

namespace Nabu.Mcp.AspNetCore.Tests.Unit
{
    public class McpConstantValueTests
    {
        [Theory]
        [InlineData("status=open", "status", "open")]
        [InlineData("  status  =open", "status", "open")]
        [InlineData("filter=a=b", "filter", "a=b")]
        [InlineData("flag", "flag", "")]
        [InlineData("pageSize=", "pageSize", "")]
        public void TrySplit_takes_everything_after_the_first_equals_as_the_value(
            string entry,
            string expectedName,
            string expectedValue)
        {
            string name;
            string value;

            Assert.True(McpConstantValue.TrySplit(entry, out name, out value));
            Assert.Equal(expectedName, name);
            Assert.Equal(expectedValue, value);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("=orphan")]
        public void TrySplit_rejects_entries_without_a_name(string? entry)
        {
            string name;
            string value;

            Assert.False(McpConstantValue.TrySplit(entry, out name, out value));
        }

        [Fact]
        public void Strings_are_passed_through_untouched()
        {
            var value = McpConstantValue.Convert("  spaced  ", typeof(string));
            Assert.Equal("  spaced  ", value!.GetValue<string>());

            // "null" is a legitimate string, not an absent value.
            Assert.Equal("null", McpConstantValue.Convert("null", typeof(string))!.GetValue<string>());
        }

        [Theory]
        [InlineData("42", typeof(int))]
        [InlineData("42", typeof(long))]
        [InlineData("42", typeof(int?))]
        public void Integers_become_json_numbers(string raw, Type type)
        {
            var value = McpConstantValue.Convert(raw, type);
            Assert.Equal(JsonValueKind.Number, value!.GetValueKind());
            Assert.Equal(42, value.GetValue<long>());
        }

        [Fact]
        public void Decimals_become_json_numbers()
        {
            var value = McpConstantValue.Convert("1.5", typeof(double));
            Assert.Equal(JsonValueKind.Number, value!.GetValueKind());
            Assert.Equal(1.5m, value.GetValue<decimal>());
        }

        [Theory]
        [InlineData("false", false)]
        [InlineData("True", true)]
        public void Booleans_become_json_booleans(string raw, bool expected)
        {
            var value = McpConstantValue.Convert(raw, typeof(bool?));
            Assert.Equal(expected, value!.GetValue<bool>());
        }

        [Fact]
        public void Enums_stay_textual_so_the_body_coercion_can_map_them()
        {
            var value = McpConstantValue.Convert("High", typeof(DayOfWeek?));
            Assert.Equal(JsonValueKind.String, value!.GetValueKind());
        }

        [Fact]
        public void Complex_parameters_accept_a_json_literal()
        {
            var value = McpConstantValue.Convert("[\"docs\",\"ops\"]", typeof(List<string>));
            Assert.Equal(JsonValueKind.Array, value!.GetValueKind());
            Assert.Equal(2, value.AsArray().Count);
        }

        [Fact]
        public void A_value_that_is_not_valid_json_falls_back_to_a_string()
        {
            var value = McpConstantValue.Convert("{not json", typeof(List<string>));
            Assert.Equal(JsonValueKind.String, value!.GetValueKind());
        }

        [Theory]
        [InlineData("", typeof(int?))]
        [InlineData("null", typeof(int?))]
        [InlineData("NULL", typeof(bool))]
        public void An_empty_or_null_value_on_a_non_string_parameter_sends_nothing(string raw, Type type)
        {
            Assert.Null(McpConstantValue.Convert(raw, type));
        }

        [Fact]
        public void A_number_that_does_not_parse_falls_back_to_a_string_so_the_binder_can_complain()
        {
            var value = McpConstantValue.Convert("not-a-number", typeof(int));
            Assert.Equal(JsonValueKind.String, value!.GetValueKind());
        }
    }
}
