using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Nabu.Mcp.AspNetCore.Schema
{
    /// <summary>
    /// Looks up <c>&lt;summary&gt;</c> and <c>&lt;param&gt;</c> text from the XML documentation file
    /// produced next to an assembly. Missing files are simply treated as "no documentation".
    /// </summary>
    public interface IXmlDocumentationProvider
    {
        string? GetSummary(MemberInfo member);

        string? GetParameterDescription(MethodBase method, string parameterName);

        string? GetReturnsDescription(MethodBase method);
    }

    /// <inheritdoc />
    public sealed class XmlDocumentationProvider : IXmlDocumentationProvider
    {
        private readonly ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, XElement>> _cache =
            new ConcurrentDictionary<Assembly, IReadOnlyDictionary<string, XElement>>();

        private static readonly IReadOnlyDictionary<string, XElement> Empty = new Dictionary<string, XElement>();

        public string? GetSummary(MemberInfo member)
        {
            var element = FindElement(member);
            return element == null ? null : ReadChild(element, "summary");
        }

        public string? GetParameterDescription(MethodBase method, string parameterName)
        {
            var element = FindElement(method);
            if (element == null)
            {
                return null;
            }

            var parameter = element
                .Elements("param")
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), parameterName, StringComparison.Ordinal));

            return parameter == null ? null : Normalize(parameter);
        }

        public string? GetReturnsDescription(MethodBase method)
        {
            var element = FindElement(method);
            return element == null ? null : ReadChild(element, "returns");
        }

        private static string? ReadChild(XElement element, string name)
        {
            var child = element.Element(name);
            return child == null ? null : Normalize(child);
        }

        private XElement? FindElement(MemberInfo member)
        {
            var assembly = member is Type type ? type.Assembly : member.DeclaringType?.Assembly;
            if (assembly == null)
            {
                return null;
            }

            var documentation = _cache.GetOrAdd(assembly, Load);
            var key = GetMemberKey(member);
            if (key == null)
            {
                return null;
            }

            XElement? element;
            return documentation.TryGetValue(key, out element) ? element : null;
        }

        private static IReadOnlyDictionary<string, XElement> Load(Assembly assembly)
        {
            try
            {
                var location = assembly.IsDynamic ? null : assembly.Location;
                if (string.IsNullOrEmpty(location))
                {
                    return Empty;
                }

                var path = Path.ChangeExtension(location, ".xml");
                if (!File.Exists(path))
                {
                    var alternative = Path.Combine(
                        AppContext.BaseDirectory ?? string.Empty,
                        Path.GetFileNameWithoutExtension(location) + ".xml");
                    if (!File.Exists(alternative))
                    {
                        return Empty;
                    }

                    path = alternative;
                }

                var document = XDocument.Load(path);
                var members = document.Root?.Element("members");
                if (members == null)
                {
                    return Empty;
                }

                var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
                foreach (var member in members.Elements("member"))
                {
                    var name = (string?)member.Attribute("name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        result[name!] = member;
                    }
                }

                return result;
            }
            catch (Exception)
            {
                // Documentation is a nice-to-have; never let a malformed file break tool discovery.
                return Empty;
            }
        }

        internal static string? GetMemberKey(MemberInfo member)
        {
            if (member is Type type)
            {
                return "T:" + GetTypeName(type);
            }

            var declaring = member.DeclaringType;
            if (declaring == null)
            {
                return null;
            }

            var prefix = declaring.FullName?.Replace('+', '.');
            if (prefix == null)
            {
                return null;
            }

            if (member is PropertyInfo property)
            {
                return "P:" + prefix + "." + property.Name;
            }

            if (member is FieldInfo field)
            {
                return "F:" + prefix + "." + field.Name;
            }

            if (member is MethodBase method)
            {
                var builder = new StringBuilder("M:").Append(prefix).Append('.').Append(method.Name.Replace('.', '#'));
                if (method is MethodInfo methodInfo && methodInfo.IsGenericMethodDefinition)
                {
                    builder.Append("``").Append(methodInfo.GetGenericArguments().Length);
                }

                var parameters = method.GetParameters();
                if (parameters.Length > 0)
                {
                    builder.Append('(');
                    for (var i = 0; i < parameters.Length; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append(GetTypeName(parameters[i].ParameterType));
                    }

                    builder.Append(')');
                }

                return builder.ToString();
            }

            return null;
        }

        private static string GetTypeName(Type type)
        {
            if (type.IsByRef)
            {
                return GetTypeName(type.GetElementType()!) + "@";
            }

            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                var suffix = rank == 1 ? "[]" : "[" + new string(',', rank - 1) + "]";
                return GetTypeName(type.GetElementType()!) + suffix;
            }

            if (type.IsGenericParameter)
            {
                return "`" + type.GenericParameterPosition;
            }

            if (type.IsConstructedGenericType)
            {
                var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
                var tick = definition.IndexOf('`');
                if (tick >= 0)
                {
                    definition = definition.Substring(0, tick);
                }

                var arguments = string.Join(",", type.GetGenericArguments().Select(GetTypeName));
                return definition.Replace('+', '.') + "{" + arguments + "}";
            }

            return (type.FullName ?? type.Name).Replace('+', '.');
        }

        private static string? Normalize(XElement element)
        {
            var builder = new StringBuilder();
            foreach (var node in element.Nodes())
            {
                switch (node)
                {
                    case XText text:
                        builder.Append(text.Value);
                        break;
                    case XElement child when child.Name == "see" || child.Name == "seealso":
                        var reference = (string?)child.Attribute("cref") ?? (string?)child.Attribute("langword");
                        if (!string.IsNullOrEmpty(reference))
                        {
                            var separator = reference!.IndexOf(':');
                            builder.Append(separator >= 0 ? reference.Substring(separator + 1) : reference);
                        }

                        break;
                    case XElement child when child.Name == "paramref" || child.Name == "typeparamref":
                        builder.Append((string?)child.Attribute("name"));
                        break;
                    case XElement child:
                        builder.Append(child.Value);
                        break;
                }
            }

            var lines = builder.ToString()
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0);

            var result = string.Join(" ", lines).Trim();
            return result.Length == 0 ? null : result;
        }
    }

    /// <summary>A provider that never returns documentation. Used when XML docs are disabled.</summary>
    public sealed class NullXmlDocumentationProvider : IXmlDocumentationProvider
    {
        public static readonly NullXmlDocumentationProvider Instance = new NullXmlDocumentationProvider();

        public string? GetSummary(MemberInfo member) => null;

        public string? GetParameterDescription(MethodBase method, string parameterName) => null;

        public string? GetReturnsDescription(MethodBase method) => null;
    }
}
