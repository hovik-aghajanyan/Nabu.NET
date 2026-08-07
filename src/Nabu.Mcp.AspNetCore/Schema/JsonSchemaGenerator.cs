using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nabu.Mcp.AspNetCore.Schema
{
    /// <summary>Builds JSON Schema fragments describing CLR types and action parameters.</summary>
    public class JsonSchemaGenerator
    {
        private static readonly HashSet<Type> IntegerTypes = new HashSet<Type>
        {
            typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong),
        };

        private static readonly HashSet<Type> NumberTypes = new HashSet<Type>
        {
            typeof(float), typeof(double), typeof(decimal),
        };

        private readonly IXmlDocumentationProvider _documentation;
        private readonly int _maxDepth;

        public JsonSchemaGenerator(IXmlDocumentationProvider documentation, int maxDepth = 8)
        {
            _documentation = documentation ?? NullXmlDocumentationProvider.Instance;
            _maxDepth = Math.Max(1, maxDepth);
        }

        /// <summary>Produces the schema for a single value of <paramref name="type"/>.</summary>
        public JsonObject Generate(Type type, IEnumerable<Attribute>? attributes = null, bool? nullable = null)
        {
            var schema = Build(type, new HashSet<Type>(), 0);
            if (attributes != null)
            {
                ApplyAnnotations(schema, attributes, type);
            }

            if (nullable == true)
            {
                MakeNullable(schema);
            }

            return schema;
        }

        /// <summary>One property of a request model lifted to the top level of a tool schema.</summary>
        public sealed class ExpandedProperty
        {
            public ExpandedProperty(string name, Type clrType, JsonObject schema, bool isRequired, string? description)
            {
                Name = name;
                ClrType = clrType;
                Schema = schema;
                IsRequired = isRequired;
                Description = description;
            }

            public string Name { get; }

            public Type ClrType { get; }

            public JsonObject Schema { get; }

            public bool IsRequired { get; }

            public string? Description { get; }
        }

        /// <summary>
        /// Expands a complex type into its properties, for callers that flatten a request model into
        /// the top level of a tool schema. Returns <c>false</c> when the type is not an object.
        /// </summary>
        public bool TryExpandObject(Type type, out IList<ExpandedProperty> properties)
        {
            properties = new List<ExpandedProperty>();

            if (!IsComplexObject(type))
            {
                return false;
            }

            foreach (var property in GetSchemaProperties(type))
            {
                var name = GetPropertyName(property);
                var attributes = property.GetCustomAttributes().ToList();
                var propertySchema = Build(property.PropertyType, new HashSet<Type> { type }, 1);
                ApplyAnnotations(propertySchema, attributes, property.PropertyType);

                var description = ReadDescription(attributes) ?? _documentation.GetSummary(property);
                if (!string.IsNullOrEmpty(description) && propertySchema["description"] == null)
                {
                    propertySchema["description"] = description;
                }

                properties.Add(new ExpandedProperty(
                    name,
                    property.PropertyType,
                    propertySchema,
                    IsRequiredProperty(property, attributes),
                    description ?? propertySchema["description"]?.GetValue<string>()));
            }

            return true;
        }

        /// <summary>True when the type should be represented as a JSON object with named properties.</summary>
        public static bool IsComplexObject(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (underlying.IsPrimitive || underlying.IsEnum)
            {
                return false;
            }

            if (underlying == typeof(string) || underlying == typeof(decimal) || underlying == typeof(Guid) ||
                underlying == typeof(DateTime) || underlying == typeof(DateTimeOffset) || underlying == typeof(TimeSpan) ||
                underlying == typeof(Uri) || underlying == typeof(object))
            {
                return false;
            }

            if (typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                return false;
            }

            return underlying.IsClass || (underlying.IsValueType && !underlying.IsPrimitive);
        }

        private JsonObject Build(Type type, ISet<Type> visited, int depth)
        {
            var nullableUnderlying = Nullable.GetUnderlyingType(type);
            var isNullableValueType = nullableUnderlying != null;
            var actual = nullableUnderlying ?? type;

            var schema = BuildCore(actual, visited, depth);
            if (isNullableValueType)
            {
                MakeNullable(schema);
            }

            return schema;
        }

        private JsonObject BuildCore(Type type, ISet<Type> visited, int depth)
        {
            if (type == typeof(string) || type == typeof(char))
            {
                return new JsonObject { ["type"] = "string" };
            }

            if (type == typeof(bool))
            {
                return new JsonObject { ["type"] = "boolean" };
            }

            if (IntegerTypes.Contains(type))
            {
                return new JsonObject { ["type"] = "integer" };
            }

            if (NumberTypes.Contains(type))
            {
                return new JsonObject { ["type"] = "number" };
            }

            if (type == typeof(Guid))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
            }

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
            }

            if (type == typeof(TimeSpan))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "duration" };
            }

            if (type == typeof(Uri))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "uri" };
            }

            if (type == typeof(byte[]))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "byte" };
            }

#if !NETSTANDARD2_0
            if (type == typeof(DateOnly))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "date" };
            }

            if (type == typeof(TimeOnly))
            {
                return new JsonObject { ["type"] = "string", ["format"] = "time" };
            }
#endif

            if (type.IsEnum)
            {
                var values = new JsonArray();
                foreach (var name in Enum.GetNames(type))
                {
                    values.Add(name);
                }

                return new JsonObject { ["type"] = "string", ["enum"] = values };
            }

            if (type == typeof(object) || type == typeof(JsonElement) || type == typeof(JsonNode) || type == typeof(JsonObject))
            {
                // Deliberately unconstrained: the action accepts arbitrary JSON.
                return new JsonObject();
            }

            var dictionaryValue = GetDictionaryValueType(type);
            if (dictionaryValue != null)
            {
                return new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = depth >= _maxDepth
                        ? new JsonObject()
                        : Build(dictionaryValue, visited, depth + 1),
                };
            }

            var elementType = GetEnumerableElementType(type);
            if (elementType != null)
            {
                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = depth >= _maxDepth ? new JsonObject() : Build(elementType, visited, depth + 1),
                };
            }

            if (depth >= _maxDepth || visited.Contains(type))
            {
                // Recursive or very deep model: stop expanding but keep it a valid object schema.
                return new JsonObject { ["type"] = "object" };
            }

            var nested = new HashSet<Type>(visited) { type };
            var properties = new JsonObject();
            var required = new JsonArray();

            foreach (var property in GetSchemaProperties(type))
            {
                var attributes = property.GetCustomAttributes().ToList();
                var propertySchema = Build(property.PropertyType, nested, depth + 1);
                ApplyAnnotations(propertySchema, attributes, property.PropertyType);

                var description = ReadDescription(attributes) ?? _documentation.GetSummary(property);
                if (!string.IsNullOrEmpty(description) && propertySchema["description"] == null)
                {
                    propertySchema["description"] = description;
                }

                var name = GetPropertyName(property);
                properties[name] = propertySchema;

                if (IsRequiredProperty(property, attributes))
                {
                    required.Add(name);
                }
            }

            var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
            if (required.Count > 0)
            {
                schema["required"] = required;
            }

            var typeDescription = ReadDescription(type.GetCustomAttributes().ToList()) ?? _documentation.GetSummary(type);
            if (!string.IsNullOrEmpty(typeDescription))
            {
                schema["description"] = typeDescription;
            }

            return schema;
        }

        internal static IEnumerable<PropertyInfo> GetSchemaProperties(Type type)
        {
            return type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Where(p => p.GetCustomAttribute<McpIgnoreAttribute>() == null)
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                .Where(p => !HasAttributeNamed(p, "System.Text.Json.Serialization.JsonIgnoreAttribute") ||
                            p.GetCustomAttribute<JsonIgnoreAttribute>() == null);
        }

        private static bool HasAttributeNamed(MemberInfo member, string fullName)
        {
            return member.GetCustomAttributes().Any(a => a.GetType().FullName == fullName);
        }

        internal static string GetPropertyName(PropertyInfo property)
        {
            var explicitName = property.GetCustomAttribute<McpParameterAttribute>()?.Name;
            if (!string.IsNullOrEmpty(explicitName))
            {
                return explicitName!;
            }

            var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
            if (!string.IsNullOrEmpty(jsonName))
            {
                return jsonName!;
            }

            return JsonNamingPolicy.CamelCase.ConvertName(property.Name);
        }

        private static bool IsRequiredProperty(PropertyInfo property, IList<Attribute> attributes)
        {
            var explicitRequired = property.GetCustomAttribute<McpParameterAttribute>()?.RequiredOverride;
            if (explicitRequired.HasValue)
            {
                return explicitRequired.Value;
            }

            if (attributes.Any(a => a is RequiredAttribute))
            {
                return true;
            }

            if (attributes.Any(a => a.GetType().FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute"))
            {
                return true;
            }

            var type = property.PropertyType;
            if (type.IsValueType && Nullable.GetUnderlyingType(type) == null)
            {
                // A non-nullable value type always round-trips with *some* value, so requiring it would
                // be noise. Only annotated members are treated as required here.
                return false;
            }

            return NullabilityHelper.IsNullable(property) == false;
        }

        internal void ApplyAnnotations(JsonObject schema, IEnumerable<Attribute> attributes, Type type)
        {
            foreach (var attribute in attributes)
            {
                switch (attribute)
                {
                    case DescriptionAttribute description when !string.IsNullOrEmpty(description.Description):
                        schema["description"] = description.Description;
                        break;
                    case DisplayAttribute display when !string.IsNullOrEmpty(display.Description):
                        schema["description"] = display.Description;
                        break;
                    case McpParameterAttribute mcp:
                        if (!string.IsNullOrEmpty(mcp.Description))
                        {
                            schema["description"] = mcp.Description;
                        }

                        if (mcp.Example != null)
                        {
                            var example = JsonHelpers.ToNode(mcp.Example);
                            if (example != null)
                            {
                                schema["examples"] = new JsonArray(example);
                            }
                        }

                        break;
                    case RangeAttribute range:
                        if (range.Minimum is IConvertible min)
                        {
                            schema["minimum"] = JsonValue.Create(Convert.ToDouble(min, System.Globalization.CultureInfo.InvariantCulture));
                        }

                        if (range.Maximum is IConvertible max)
                        {
                            schema["maximum"] = JsonValue.Create(Convert.ToDouble(max, System.Globalization.CultureInfo.InvariantCulture));
                        }

                        break;
                    case StringLengthAttribute stringLength:
                        if (stringLength.MinimumLength > 0)
                        {
                            schema["minLength"] = stringLength.MinimumLength;
                        }

                        schema["maxLength"] = stringLength.MaximumLength;
                        break;
                    case MinLengthAttribute minLength:
                        schema[IsArraySchema(schema) ? "minItems" : "minLength"] = minLength.Length;
                        break;
                    case MaxLengthAttribute maxLength:
                        schema[IsArraySchema(schema) ? "maxItems" : "maxLength"] = maxLength.Length;
                        break;
                    case RegularExpressionAttribute pattern when !string.IsNullOrEmpty(pattern.Pattern):
                        schema["pattern"] = pattern.Pattern;
                        break;
                    case EmailAddressAttribute _:
                        schema["format"] = "email";
                        break;
                    case UrlAttribute _:
                        schema["format"] = "uri";
                        break;
                    case DefaultValueAttribute defaultValue when defaultValue.Value != null:
                        schema["default"] = JsonHelpers.ToNode(defaultValue.Value);
                        break;
                }
            }
        }

        private static bool IsArraySchema(JsonObject schema)
        {
            var node = schema["type"];
            return node != null && node.GetValueKind() == JsonValueKind.String && node.GetValue<string>() == "array";
        }

        internal static string? ReadDescription(IEnumerable<Attribute> attributes)
        {
            foreach (var attribute in attributes)
            {
                switch (attribute)
                {
                    case McpParameterAttribute mcp when !string.IsNullOrEmpty(mcp.Description):
                        return mcp.Description;
                    case DescriptionAttribute description when !string.IsNullOrEmpty(description.Description):
                        return description.Description;
                    case DisplayAttribute display when !string.IsNullOrEmpty(display.Description):
                        return display.Description;
                }
            }

            return null;
        }

        private static void MakeNullable(JsonObject schema)
        {
            var type = schema["type"];
            if (type == null || type.GetValueKind() != JsonValueKind.String)
            {
                return;
            }

            var value = type.GetValue<string>();
            if (value == "null")
            {
                return;
            }

            schema["type"] = new JsonArray(value, "null");
        }

        internal static Type? GetDictionaryValueType(Type type)
        {
            foreach (var candidate in GetSelfAndInterfaces(type))
            {
                if (!candidate.IsConstructedGenericType)
                {
                    continue;
                }

                var definition = candidate.GetGenericTypeDefinition();
                if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                {
                    var arguments = candidate.GetGenericArguments();
                    if (arguments[0] == typeof(string))
                    {
                        return arguments[1];
                    }
                }
            }

            return null;
        }

        internal static Type? GetEnumerableElementType(Type type)
        {
            if (type == typeof(string))
            {
                return null;
            }

            if (type.IsArray)
            {
                return type.GetElementType();
            }

            foreach (var candidate in GetSelfAndInterfaces(type))
            {
                if (candidate.IsConstructedGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    return candidate.GetGenericArguments()[0];
                }
            }

            return typeof(IEnumerable).IsAssignableFrom(type) ? typeof(object) : null;
        }

        private static IEnumerable<Type> GetSelfAndInterfaces(Type type)
        {
            yield return type;
            foreach (var @interface in type.GetInterfaces())
            {
                yield return @interface;
            }
        }
    }
}
