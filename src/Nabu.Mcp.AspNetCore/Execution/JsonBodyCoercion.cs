using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nabu.Mcp.AspNetCore.Schema;

namespace Nabu.Mcp.AspNetCore.Execution
{
    /// <summary>
    /// Rewrites the JSON body Nabu builds so it matches what the application's input formatter expects.
    /// Today that means turning enum names - which is how tool schemas describe enums - back into their
    /// numeric values when the application does not use a string enum converter.
    /// </summary>
    internal static class JsonBodyCoercion
    {
        public static JsonNode? Coerce(JsonNode? node, Type? clrType, McpJsonCompatibility compatibility)
        {
            if (node == null || clrType == null || compatibility.StringEnumsEnabled)
            {
                return node;
            }

            return CoerceCore(node, clrType, compatibility, 0);
        }

        private static JsonNode? CoerceCore(JsonNode? node, Type? clrType, McpJsonCompatibility compatibility, int depth)
        {
            if (node == null || clrType == null || depth > 16)
            {
                return node;
            }

            var type = Nullable.GetUnderlyingType(clrType) ?? clrType;

            if (type.IsEnum)
            {
                return CoerceEnum(node, type);
            }

            if (node is JsonArray array)
            {
                var elementType = JsonSchemaGenerator.GetEnumerableElementType(type);
                if (elementType == null || elementType == typeof(object))
                {
                    return node;
                }

                for (var i = 0; i < array.Count; i++)
                {
                    var original = array[i];
                    if (original == null)
                    {
                        continue;
                    }

                    var detached = JsonNode.Parse(original.ToJsonString());
                    var coerced = CoerceCore(detached, elementType, compatibility, depth + 1);
                    array[i] = coerced;
                }

                return array;
            }

            if (node is JsonObject obj)
            {
                var valueType = JsonSchemaGenerator.GetDictionaryValueType(type);
                if (valueType != null)
                {
                    foreach (var key in new List<string>(GetKeys(obj)))
                    {
                        var original = obj[key];
                        if (original == null)
                        {
                            continue;
                        }

                        var detached = JsonNode.Parse(original.ToJsonString());
                        obj[key] = CoerceCore(detached, valueType, compatibility, depth + 1);
                    }

                    return obj;
                }

                if (!JsonSchemaGenerator.IsComplexObject(type))
                {
                    return obj;
                }

                var properties = BuildPropertyMap(type);
                foreach (var key in new List<string>(GetKeys(obj)))
                {
                    PropertyInfo? property;
                    if (!properties.TryGetValue(key, out property))
                    {
                        continue;
                    }

                    var original = obj[key];
                    if (original == null)
                    {
                        continue;
                    }

                    var detached = JsonNode.Parse(original.ToJsonString());
                    obj[key] = CoerceCore(detached, property.PropertyType, compatibility, depth + 1);
                }

                return obj;
            }

            return node;
        }

        private static IEnumerable<string> GetKeys(JsonObject obj)
        {
            foreach (var pair in obj)
            {
                yield return pair.Key;
            }
        }

        private static JsonNode? CoerceEnum(JsonNode node, Type enumType)
        {
            if (node.GetValueKind() != JsonValueKind.String)
            {
                return node;
            }

            if (McpJsonCompatibility.HasStringEnumConverterAttribute(enumType))
            {
                return node;
            }

            var text = node.GetValue<string>();
            if (string.IsNullOrEmpty(text))
            {
                return node;
            }

            object parsed;
            try
            {
                parsed = Enum.Parse(enumType, text, ignoreCase: true);
            }
            catch (ArgumentException)
            {
                // Leave the value alone so the application returns its own validation error.
                return node;
            }
            catch (OverflowException)
            {
                return node;
            }

            var underlying = Convert.ChangeType(
                parsed,
                Enum.GetUnderlyingType(enumType),
                System.Globalization.CultureInfo.InvariantCulture);

            return JsonNode.Parse(JsonSerializer.Serialize(underlying));
        }

        private static IDictionary<string, PropertyInfo> BuildPropertyMap(Type type)
        {
            var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in JsonSchemaGenerator.GetSchemaProperties(type))
            {
                map[JsonSchemaGenerator.GetPropertyName(property)] = property;
                map[property.Name] = property;
            }

            return map;
        }
    }
}
