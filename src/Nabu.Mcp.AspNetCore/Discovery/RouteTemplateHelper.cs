using System;
using System.Collections.Generic;
using System.Text;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>
    /// Minimal route template parsing: enough to find the token names of a template and to substitute
    /// values back into it. Constraints, defaults, optional markers and catch-all markers are honoured.
    /// </summary>
    internal static class RouteTemplateHelper
    {
        /// <summary>Returns the parameter names appearing in <paramref name="template"/>.</summary>
        public static IReadOnlyList<string> GetTokenNames(string template)
        {
            var names = new List<string>();
            foreach (var token in EnumerateTokens(template))
            {
                names.Add(token.Name);
            }

            return names;
        }

        public static bool ContainsToken(string template, string name)
        {
            foreach (var token in EnumerateTokens(template))
            {
                if (string.Equals(token.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Replaces every token with the value returned by <paramref name="resolve"/>. Tokens that are
        /// optional or have a default are dropped when no value is supplied; a missing required token
        /// throws.
        /// </summary>
        public static string Substitute(string template, Func<string, string?> resolve)
        {
            return Substitute(template, resolve, escapeValues: true);
        }

        private static string Substitute(string template, Func<string, string?> resolve, bool escapeValues)
        {
            var builder = new StringBuilder(template.Length + 16);
            var index = 0;

            while (index < template.Length)
            {
                var open = template.IndexOf('{', index);
                if (open < 0)
                {
                    AppendLiteral(builder, template, index, template.Length - index);
                    break;
                }

                // "{{" is an escaped brace.
                if (open + 1 < template.Length && template[open + 1] == '{')
                {
                    AppendLiteral(builder, template, index, open - index);
                    builder.Append('{');
                    index = open + 2;
                    continue;
                }

                var close = FindClosingBrace(template, open);
                if (close < 0)
                {
                    AppendLiteral(builder, template, index, template.Length - index);
                    break;
                }

                AppendLiteral(builder, template, index, open - index);

                var token = ParseToken(template.Substring(open + 1, close - open - 1));
                var value = resolve(token.Name);

                if (value == null || value.Length == 0)
                {
                    if (token.IsOptional || token.IsCatchAll || token.DefaultValue != null)
                    {
                        // Drop the segment separator that introduced the optional token.
                        if (builder.Length > 0 && builder[builder.Length - 1] == '/')
                        {
                            builder.Length -= 1;
                        }

                        index = close + 1;
                        continue;
                    }

                    throw new InvalidOperationException(
                        "Route parameter '" + token.Name + "' is required by template '" + template + "' but no value was supplied.");
                }

                if (!escapeValues)
                {
                    builder.Append(value);
                }
                else
                {
                    builder.Append(token.IsCatchAll ? EncodePath(value) : Uri.EscapeDataString(value));
                }

                index = close + 1;
            }

            var result = builder.ToString();
            return result.StartsWith("/", StringComparison.Ordinal) ? result : "/" + result;
        }

        /// <summary>Strips constraints and markers, leaving <c>api/x/{id}</c> style text.</summary>
        public static string Normalize(string template)
        {
            // Values are written verbatim here: escaping would turn the braces into %7B/%7D and the
            // result would no longer be recognisable as a template.
            return Substitute(template, name => "{" + name + "}", escapeValues: false).TrimStart('/');
        }

        /// <summary>Copies literal template text, collapsing the "}}" escape back to a single brace.</summary>
        private static void AppendLiteral(StringBuilder builder, string template, int start, int length)
        {
            for (var i = start; i < start + length; i++)
            {
                builder.Append(template[i]);
                if (template[i] == '}' && i + 1 < start + length && template[i + 1] == '}')
                {
                    i++;
                }
            }
        }

        private static string EncodePath(string value)
        {
            var segments = value.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                segments[i] = Uri.EscapeDataString(segments[i]);
            }

            return string.Join("/", segments);
        }

        private static int FindClosingBrace(string template, int open)
        {
            var depth = 0;
            for (var i = open; i < template.Length; i++)
            {
                if (template[i] == '{')
                {
                    depth++;
                }
                else if (template[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static IEnumerable<RouteToken> EnumerateTokens(string template)
        {
            var index = 0;
            while (index < template.Length)
            {
                var open = template.IndexOf('{', index);
                if (open < 0)
                {
                    yield break;
                }

                if (open + 1 < template.Length && template[open + 1] == '{')
                {
                    index = open + 2;
                    continue;
                }

                var close = FindClosingBrace(template, open);
                if (close < 0)
                {
                    yield break;
                }

                yield return ParseToken(template.Substring(open + 1, close - open - 1));
                index = close + 1;
            }
        }

        private static RouteToken ParseToken(string content)
        {
            var isCatchAll = false;
            if (content.StartsWith("**", StringComparison.Ordinal))
            {
                isCatchAll = true;
                content = content.Substring(2);
            }
            else if (content.StartsWith("*", StringComparison.Ordinal))
            {
                isCatchAll = true;
                content = content.Substring(1);
            }

            string? defaultValue = null;
            var name = content;

            // Constraints are separated by ':' and may themselves contain '(...)' with colons inside.
            var constraintStart = IndexOfTopLevel(content, ':');
            var defaultStart = IndexOfTopLevel(content, '=');

            if (defaultStart >= 0 && (constraintStart < 0 || defaultStart < constraintStart))
            {
                name = content.Substring(0, defaultStart);
                var rest = content.Substring(defaultStart + 1);
                var nextConstraint = IndexOfTopLevel(rest, ':');
                defaultValue = nextConstraint >= 0 ? rest.Substring(0, nextConstraint) : rest;
            }
            else if (constraintStart >= 0)
            {
                name = content.Substring(0, constraintStart);
                var rest = content.Substring(constraintStart + 1);
                var nextDefault = IndexOfTopLevel(rest, '=');
                if (nextDefault >= 0)
                {
                    defaultValue = rest.Substring(nextDefault + 1);
                }
            }

            var isOptional = name.EndsWith("?", StringComparison.Ordinal);
            if (isOptional)
            {
                name = name.Substring(0, name.Length - 1);
            }
            else if (defaultValue == null && content.EndsWith("?", StringComparison.Ordinal))
            {
                isOptional = true;
            }

            return new RouteToken(name.Trim(), isOptional, isCatchAll, defaultValue);
        }

        private static int IndexOfTopLevel(string value, char target)
        {
            var depth = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (c == target && depth == 0)
                {
                    return i;
                }
            }

            return -1;
        }

        private readonly struct RouteToken
        {
            public RouteToken(string name, bool isOptional, bool isCatchAll, string? defaultValue)
            {
                Name = name;
                IsOptional = isOptional;
                IsCatchAll = isCatchAll;
                DefaultValue = defaultValue;
            }

            public string Name { get; }

            public bool IsOptional { get; }

            public bool IsCatchAll { get; }

            public string? DefaultValue { get; }
        }
    }
}
