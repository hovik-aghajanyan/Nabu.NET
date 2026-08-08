using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nabu.Mcp.AspNetCore.Schema;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>
    /// Parses the <c>name=value</c> entries of <see cref="McpToolAttribute.ConstantParameters"/>.
    /// Attribute arguments can only be compile-time constants, so values arrive as text and are
    /// converted here to the JSON shape the target parameter expects.
    /// </summary>
    internal static class McpConstantValue
    {
        private static readonly HashSet<Type> IntegerTypes = new HashSet<Type>
        {
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong),
        };

        private static readonly HashSet<Type> DecimalTypes = new HashSet<Type>
        {
            typeof(float), typeof(double), typeof(decimal),
        };

        /// <summary>Splits <c>"name=value"</c>. A bare name yields an empty value.</summary>
        public static bool TrySplit(string? entry, out string name, out string value)
        {
            name = string.Empty;
            value = string.Empty;

            if (string.IsNullOrWhiteSpace(entry))
            {
                return false;
            }

            var separator = entry!.IndexOf('=');
            if (separator < 0)
            {
                name = entry.Trim();
                return name.Length > 0;
            }

            name = entry.Substring(0, separator).Trim();

            // Everything after the first '=' is the value, verbatim: JSON literals and connection-string
            // style values both contain '=' of their own.
            value = entry.Substring(separator + 1);
            return name.Length > 0;
        }

        /// <summary>Converts the textual constant to the JSON value the action's binder will accept.</summary>
        public static JsonNode? Convert(string raw, Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;

            if (underlying == typeof(string))
            {
                return JsonValue.Create(raw);
            }

            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                // An empty value on a non-string parameter means "send nothing", not "send an empty string".
                return null;
            }

            if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (underlying == typeof(bool))
            {
                bool parsedBool;
                if (bool.TryParse(trimmed, out parsedBool))
                {
                    return JsonValue.Create(parsedBool);
                }

                return JsonValue.Create(raw);
            }

            if (IntegerTypes.Contains(underlying))
            {
                long parsedLong;
                if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedLong))
                {
                    return JsonValue.Create(parsedLong);
                }

                ulong parsedULong;
                if (ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedULong))
                {
                    return JsonValue.Create(parsedULong);
                }

                return JsonValue.Create(raw);
            }

            if (DecimalTypes.Contains(underlying))
            {
                decimal parsedDecimal;
                if (decimal.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedDecimal))
                {
                    return JsonValue.Create(parsedDecimal);
                }

                double parsedDouble;
                if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedDouble))
                {
                    return JsonValue.Create(parsedDouble);
                }

                return JsonValue.Create(raw);
            }

            // Enums stay textual: tool schemas describe them by name and JsonBodyCoercion converts the
            // name to a number when the application's formatters expect numbers.
            if (underlying.IsEnum)
            {
                return JsonValue.Create(raw);
            }

            if (JsonSchemaGenerator.IsComplexObject(underlying) ||
                JsonSchemaGenerator.GetEnumerableElementType(underlying) != null)
            {
                JsonNode? parsed;
                if (TryParseJson(trimmed, out parsed))
                {
                    return parsed;
                }
            }

            return JsonValue.Create(raw);
        }

        private static bool TryParseJson(string text, out JsonNode? node)
        {
            node = null;

            var first = text[0];
            if (first != '{' && first != '[' && first != '"')
            {
                return false;
            }

            try
            {
                node = JsonNode.Parse(text);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
