using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nabu.Mcp.AspNetCore.Schema
{
    /// <summary>
    /// Small JSON helpers that behave identically across every target framework. In particular
    /// <c>JsonNode.DeepClone</c> only exists on .NET 8, and <c>JsonValue.Create(object)</c> does not
    /// handle arbitrary CLR values, so both are implemented here explicitly.
    /// </summary>
    internal static class JsonHelpers
    {
        // Deliberately no custom JsonSerializerOptions: JsonValue instances created from boxed CLR
        // values need a type info resolver, and only the default options carry one.

        /// <summary>Returns an independent copy of <paramref name="node"/>.</summary>
        public static JsonObject Clone(JsonObject node)
        {
            return JsonNode.Parse(node.ToJsonString())!.AsObject();
        }

        /// <summary>Converts an arbitrary CLR value into a JSON node, returning <c>null</c> on failure.</summary>
        public static JsonNode? ToNode(object? value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is System.Enum)
            {
                // Enums are advertised by name, so their defaults and examples must be names too.
                return JsonValue.Create(value.ToString());
            }

            try
            {
                return JsonSerializer.SerializeToNode(value, value.GetType());
            }
            catch (JsonException)
            {
                return JsonValue.Create(value.ToString());
            }
            catch (System.NotSupportedException)
            {
                return JsonValue.Create(value.ToString());
            }
        }
    }
}
