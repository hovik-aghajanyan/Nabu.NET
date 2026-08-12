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

            /// <summary>
            /// True when the target endpoint binds form data. The request body is then built from
            /// <see cref="FormFields"/> and <see cref="FormFiles"/> - multipart/form-data when a file
            /// is present, application/x-www-form-urlencoded otherwise - and <see cref="Body"/> is ignored.
            /// </summary>
            public bool IsForm { get; set; }

            /// <summary>Form fields, in write order. A repeated key sends the field several times.</summary>
            public IList<KeyValuePair<string, string>> FormFields { get; } = new List<KeyValuePair<string, string>>();

            /// <summary>Decoded file parts for the multipart/form-data body.</summary>
            public IList<McpFormFilePart> FormFiles { get; } = new List<McpFormFilePart>();

            public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// Header names whose values were pinned by the tool definition rather than supplied by the
            /// model, so <see cref="NabuMcpOptions.ProtectedHeaders"/> does not apply to them.
            /// </summary>
            public ISet<string> ConstantHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>One file of a multipart/form-data request, decoded from a tool argument.</summary>
        public sealed class McpFormFilePart
        {
            public McpFormFilePart(string name, string fileName, string contentType, byte[] content)
            {
                Name = name;
                FileName = fileName;
                ContentType = contentType;
                Content = content;
            }

            /// <summary>The form field name the action's model binding looks for.</summary>
            public string Name { get; }

            public string FileName { get; }

            public string ContentType { get; }

            public byte[] Content { get; }
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
                if (parameter.Source == McpParameterSource.Form || parameter.Source == McpParameterSource.FormFile)
                {
                    result.IsForm = true;
                    break;
                }
            }

            if (!result.IsForm)
            {
                foreach (var constant in tool.Constants)
                {
                    if (constant.Source == McpParameterSource.Form || constant.Source == McpParameterSource.FormFile)
                    {
                        result.IsForm = true;
                        break;
                    }
                }
            }

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

                    case McpParameterSource.Form:
                        if (isBodyRoot)
                        {
                            // The argument is the whole form (IFormCollection): every property of the
                            // supplied object becomes a field of its own.
                            if (value is JsonObject formRoot)
                            {
                                foreach (var property in formRoot)
                                {
                                    if (replaceExisting)
                                    {
                                        RemoveFields(result.FormFields, property.Key);
                                    }

                                    AppendQuery(result.FormFields, property.Key, property.Value);
                                }
                            }
                            else if (value != null)
                            {
                                throw new McpArgumentException(
                                    "Argument '" + bindingName + "' for tool '" + tool.Name + "' must be an object of form fields.");
                            }
                        }
                        else
                        {
                            if (replaceExisting)
                            {
                                RemoveFields(result.FormFields, bindingName);
                            }

                            AppendQuery(result.FormFields, bindingName, value);
                        }

                        break;

                    case McpParameterSource.FormFile:
                        if (replaceExisting)
                        {
                            for (var i = result.FormFiles.Count - 1; i >= 0; i--)
                            {
                                if (string.Equals(result.FormFiles[i].Name, bindingName, StringComparison.OrdinalIgnoreCase))
                                {
                                    result.FormFiles.RemoveAt(i);
                                }
                            }
                        }

                        AppendFiles(result.FormFiles, tool.Name, bindingName, value);
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

        private static void RemoveFields(IList<KeyValuePair<string, string>> fields, string key)
        {
            for (var i = fields.Count - 1; i >= 0; i--)
            {
                if (string.Equals(fields[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    fields.RemoveAt(i);
                }
            }
        }

        private static void AppendFiles(ICollection<McpFormFilePart> files, string toolName, string name, JsonNode? value)
        {
            if (value == null)
            {
                return;
            }

            if (value is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (item != null)
                    {
                        files.Add(ParseFilePart(toolName, name, item));
                    }
                }

                return;
            }

            files.Add(ParseFilePart(toolName, name, value));
        }

        /// <summary>
        /// Decodes one file argument: either an object carrying base64 <c>data</c> with an optional
        /// <c>fileName</c> and <c>contentType</c>, or - leniently - a bare base64 string.
        /// </summary>
        private static McpFormFilePart ParseFilePart(string toolName, string name, JsonNode value)
        {
            string? data;
            string? fileName = null;
            string? contentType = null;

            if (value is JsonObject obj)
            {
                data = StringProperty(obj, "data");
                fileName = StringProperty(obj, "fileName");
                contentType = StringProperty(obj, "contentType");

                if (data == null)
                {
                    throw new McpArgumentException(
                        "File argument '" + name + "' for tool '" + toolName + "' is missing its base64 'data' property.");
                }
            }
            else if (value.GetValueKind() == JsonValueKind.String)
            {
                data = value.GetValue<string>();
            }
            else
            {
                throw new McpArgumentException(
                    "File argument '" + name + "' for tool '" + toolName + "' must be an object with a base64 'data' property.");
            }

            byte[] content;
            try
            {
                content = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                throw new McpArgumentException(
                    "File argument '" + name + "' for tool '" + toolName + "' does not contain valid base64 data.");
            }

            return new McpFormFilePart(
                name,
                string.IsNullOrEmpty(fileName) ? name : fileName!,
                string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType!,
                content);
        }

        private static string? StringProperty(JsonObject obj, string name)
        {
            JsonNode? node;
            if (!obj.TryGetPropertyValue(name, out node) || node == null)
            {
                return null;
            }

            return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
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
