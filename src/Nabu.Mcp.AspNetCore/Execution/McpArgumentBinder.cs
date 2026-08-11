using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nabu.Mcp.AspNetCore.Discovery;

namespace Nabu.Mcp.AspNetCore.Execution
{
    /// <summary>Turns MCP tool arguments into the path, query string and JSON body of an HTTP request.</summary>
    internal static class McpArgumentBinder
    {
        public sealed class BoundRequest
        {
            public string Path { get; set; } = "/";

            public string QueryString { get; set; } = string.Empty;

            public JsonNode? Body { get; set; }

            public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Header names whose values were pinned by the tool definition rather than supplied by the
            /// model, so <see cref="NabuMcpOptions.ProtectedHeaders"/> does not apply to them.
            /// </summary>
            public ISet<string> ConstantHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public static BoundRequest Bind(McpToolDescriptor tool, JsonObject? arguments, Schema.McpJsonCompatibility compatibility)
        {
            var result = new BoundRequest();
            var routeValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var query = new List<KeyValuePair<string, string>>();
            JsonObject? bodyObject = null;
            JsonNode? bodyRoot = null;

            foreach (var parameter in tool.Parameters)
            {
                JsonNode? value = null;
                var present = arguments != null && arguments.TryGetPropertyValue(parameter.Name, out value);

                if (!present || value == null)
                {
                    if (parameter.IsRequired && !present)
                    {
                        throw new McpArgumentException(
                            "Missing required argument '" + parameter.Name + "' for tool '" + tool.Name + "'.");
                    }

                    if (!present)
                    {
                        continue;
                    }
                }

                Write(
                    parameter.Source,
                    parameter.BindingName,
                    parameter.ParameterType,
                    parameter.IsBodyRoot,
                    value,
                    replaceExisting: false);
            }

            // Constants are written last so a value pinned by the tool definition always wins over an
            // argument that happens to bind to the same place.
            foreach (var constant in tool.Constants)
            {
                if (constant.Value == null)
                {
                    continue;
                }

                Write(
                    constant.Source,
                    constant.BindingName,
                    constant.ParameterType,
                    constant.IsBodyRoot,
                    constant.Value,
                    replaceExisting: true);
            }

            void Write(
                McpParameterSource source,
                string bindingName,
                Type parameterType,
                bool isBodyRoot,
                JsonNode? value,
                bool replaceExisting)
            {
                switch (source)
                {
                    case McpParameterSource.Route:
                        routeValues[bindingName] = ToScalar(value) ?? string.Empty;
                        break;

                    case McpParameterSource.Header:
                        var header = ToScalar(value);
                        if (header != null)
                        {
                            result.Headers[bindingName] = header;

                            // Constants take the replaceExisting path; remember them so protected-header
                            // enforcement can tell developer-pinned values from model-supplied ones.
                            if (replaceExisting)
                            {
                                result.ConstantHeaders.Add(bindingName);
                            }
                            else
                            {
                                result.ConstantHeaders.Remove(bindingName);
                            }
                        }

                        break;

                    case McpParameterSource.Query:
                        if (replaceExisting)
                        {
                            for (var i = query.Count - 1; i >= 0; i--)
                            {
                                if (string.Equals(query[i].Key, bindingName, StringComparison.OrdinalIgnoreCase))
                                {
                                    query.RemoveAt(i);
                                }
                            }
                        }

                        AppendQuery(query, bindingName, value);
                        break;

                    case McpParameterSource.Body:
                        var detached = value == null ? null : JsonNode.Parse(value.ToJsonString());
                        detached = JsonBodyCoercion.Coerce(detached, parameterType, compatibility);

                        if (isBodyRoot)
                        {
                            bodyRoot = detached;
                        }
                        else
                        {
                            bodyObject ??= new JsonObject();
                            bodyObject[bindingName] = detached;
                        }

                        break;
                }
            }

            result.Path = RouteTemplateHelper.Substitute(tool.RouteTemplate, name =>
            {
                string? value;
                return routeValues.TryGetValue(name, out value) ? value : null;
            });

            result.QueryString = BuildQueryString(query);
            result.Body = bodyRoot ?? bodyObject;

            return result;
        }

        private static void AppendQuery(ICollection<KeyValuePair<string, string>> query, string key, JsonNode? value)
        {
            if (value == null)
            {
                return;
            }

            switch (value)
            {
                case JsonArray array:
                    var index = 0;
                    foreach (var item in array)
                    {
                        if (item is JsonObject || item is JsonArray)
                        {
                            AppendQuery(query, key + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", item);
                        }
                        else
                        {
                            var scalar = ToScalar(item);
                            if (scalar != null)
                            {
                                query.Add(new KeyValuePair<string, string>(key, scalar));
                            }
                        }

                        index++;
                    }

                    break;

                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        AppendQuery(query, key + "." + property.Key, property.Value);
                    }

                    break;

                default:
                    var text = ToScalar(value);
                    if (text != null)
                    {
                        query.Add(new KeyValuePair<string, string>(key, text));
                    }

                    break;
            }
        }

        private static string BuildQueryString(IList<KeyValuePair<string, string>> query)
        {
            if (query.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder("?");
            for (var i = 0; i < query.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('&');
                }

                builder.Append(Uri.EscapeDataString(query[i].Key))
                       .Append('=')
                       .Append(Uri.EscapeDataString(query[i].Value));
            }

            return builder.ToString();
        }

        /// <summary>Renders a JSON value the way it would appear in a query string or route segment.</summary>
        internal static string? ToScalar(JsonNode? node)
        {
            if (node == null)
            {
                return null;
            }

            switch (node.GetValueKind())
            {
                case JsonValueKind.String:
                    return node.GetValue<string>();
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.Number:
                    return node.ToJsonString();
                default:
                    return node.ToJsonString();
            }
        }
    }

    /// <summary>Raised when tool arguments cannot be mapped onto the action's parameters.</summary>
    public sealed class McpArgumentException : Exception
    {
        public McpArgumentException(string message)
            : base(message)
        {
        }
    }
}
