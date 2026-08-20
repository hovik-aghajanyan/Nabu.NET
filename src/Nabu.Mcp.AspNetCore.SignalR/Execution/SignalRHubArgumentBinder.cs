using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nabu.Mcp.AspNetCore.Discovery;
using Nabu.Mcp.AspNetCore.Execution;
using Nabu.Mcp.AspNetCore.Schema;
using Nabu.Mcp.AspNetCore.SignalR.Discovery;

namespace Nabu.Mcp.AspNetCore.SignalR.Execution
{
    /// <summary>
    /// Turns the named arguments of a tool call into the positional argument array a hub method
    /// invocation carries. Constants pinned by the tool definition always win over caller input,
    /// omitted optional parameters fall back to their declared defaults, and enum names are
    /// converted to numbers when the hub's payload serializer would not accept them.
    /// </summary>
    internal static class SignalRHubArgumentBinder
    {
        public static JsonNode?[] Bind(
            McpToolDescriptor tool,
            SignalRHubToolMetadata metadata,
            JsonObject? arguments,
            McpJsonCompatibility compatibility)
        {
            var parameters = metadata.InvocationParameters;
            var result = new JsonNode?[parameters.Count];

            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                var bindingName = parameter.Name!;

                var constant = FindConstant(tool, bindingName);
                if (constant != null)
                {
                    result[i] = Detach(constant.Value);
                    continue;
                }

                var descriptor = FindParameter(tool, bindingName);
                JsonNode? value = null;
                var supplied = descriptor != null
                    && arguments != null
                    && arguments.TryGetPropertyValue(descriptor.Name, out value);

                if (supplied)
                {
                    result[i] = JsonBodyCoercion.Coerce(Detach(value), parameter.ParameterType, compatibility);
                    continue;
                }

                if (descriptor != null && descriptor.IsRequired)
                {
                    throw new McpArgumentException("The required argument '" + descriptor.Name + "' is missing.");
                }

                if (parameter.HasDefaultValue)
                {
                    result[i] = parameter.DefaultValue == null
                        ? null
                        : JsonSerializer.SerializeToNode(parameter.DefaultValue, parameter.ParameterType);
                    continue;
                }

                // A hidden or omitted parameter with no default: the invocation still has to carry
                // something in its position, and null is the only honest value. Discovery only lets
                // this happen for nullable parameters.
                result[i] = null;
            }

            return result;
        }

        private static McpToolConstantDescriptor? FindConstant(McpToolDescriptor tool, string bindingName)
        {
            foreach (var constant in tool.Constants)
            {
                if (string.Equals(constant.BindingName, bindingName, StringComparison.OrdinalIgnoreCase))
                {
                    return constant;
                }
            }

            return null;
        }

        private static McpToolParameterDescriptor? FindParameter(McpToolDescriptor tool, string bindingName)
        {
            foreach (var parameter in tool.Parameters)
            {
                if (string.Equals(parameter.BindingName, bindingName, StringComparison.OrdinalIgnoreCase))
                {
                    return parameter;
                }
            }

            return null;
        }

        /// <summary>JsonNode instances are single-parent; clone before placing into a new tree.</summary>
        private static JsonNode? Detach(JsonNode? node)
        {
            return node?.DeepClone();
        }
    }
}
