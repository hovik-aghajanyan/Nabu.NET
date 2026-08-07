using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Nabu.Mcp.AspNetCore.Schema
{
    /// <summary>
    /// Works out how the host application's JSON stack represents enums on the wire, so that request
    /// bodies Nabu builds are accepted by the very same input formatter the API already uses.
    /// </summary>
    /// <remarks>
    /// Tool schemas always describe enums by name, because names are what a model can reason about.
    /// When the application serializes enums as numbers - which is the default for both System.Text.Json
    /// and Newtonsoft.Json - Nabu converts the names back to their numeric values before writing the
    /// request body. Detection is done reflectively so the same code works from ASP.NET Core 2.1 to 10.
    /// </remarks>
    public sealed class McpJsonCompatibility
    {
        private readonly bool _stringEnumsEnabled;

        public McpJsonCompatibility(bool stringEnumsEnabled)
        {
            _stringEnumsEnabled = stringEnumsEnabled;
        }

        /// <summary>True when the application accepts enum names in JSON request bodies.</summary>
        public bool StringEnumsEnabled
        {
            get { return _stringEnumsEnabled; }
        }

        /// <summary>
        /// Detects the application's enum representation, honouring an explicit override.
        /// </summary>
        public static McpJsonCompatibility Detect(IServiceProvider services, bool? explicitSetting)
        {
            if (explicitSetting.HasValue)
            {
                return new McpJsonCompatibility(explicitSetting.Value);
            }

            return new McpJsonCompatibility(DetectStringEnums(services));
        }

        /// <summary>
        /// True when this specific enum type opts into name-based serialization through a
        /// <c>[JsonConverter]</c> attribute, regardless of the global setting.
        /// </summary>
        public static bool HasStringEnumConverterAttribute(Type enumType)
        {
            var attribute = enumType.GetCustomAttribute<JsonConverterAttribute>();
            if (attribute?.ConverterType == null)
            {
                return false;
            }

            return typeof(JsonStringEnumConverter).IsAssignableFrom(attribute.ConverterType);
        }

        private static bool DetectStringEnums(IServiceProvider services)
        {
            if (services == null)
            {
                return false;
            }

            // System.Text.Json (ASP.NET Core 3.0+).
            if (TryReadConverters(services, "Microsoft.AspNetCore.Mvc.JsonOptions", "JsonSerializerOptions", out var converters) &&
                ContainsConverterNamed(converters, "JsonStringEnumConverter"))
            {
                return true;
            }

            // Newtonsoft.Json, either as the 2.x default or through AddNewtonsoftJson.
            foreach (var optionsTypeName in new[]
                     {
                         "Microsoft.AspNetCore.Mvc.MvcNewtonsoftJsonOptions",
                         "Microsoft.AspNetCore.Mvc.MvcJsonOptions",
                     })
            {
                if (TryReadConverters(services, optionsTypeName, "SerializerSettings", out converters) &&
                    ContainsConverterNamed(converters, "StringEnumConverter"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadConverters(
            IServiceProvider services,
            string optionsTypeName,
            string settingsPropertyName,
            out IEnumerable? converters)
        {
            converters = null;

            var optionsType = FindType(optionsTypeName);
            if (optionsType == null)
            {
                return false;
            }

            object? options;
            try
            {
                var accessorType = typeof(Microsoft.Extensions.Options.IOptions<>).MakeGenericType(optionsType);
                var accessor = services.GetService(accessorType);
                if (accessor == null)
                {
                    return false;
                }

                options = accessorType.GetProperty("Value")?.GetValue(accessor);
            }
            catch (Exception)
            {
                return false;
            }

            if (options == null)
            {
                return false;
            }

            var settings = optionsType.GetProperty(settingsPropertyName)?.GetValue(options);
            if (settings == null)
            {
                return false;
            }

            converters = settings.GetType().GetProperty("Converters")?.GetValue(settings) as IEnumerable;
            return converters != null;
        }

        private static bool ContainsConverterNamed(IEnumerable? converters, string typeName)
        {
            if (converters == null)
            {
                return false;
            }

            foreach (var converter in converters)
            {
                if (converter == null)
                {
                    continue;
                }

                for (var type = converter.GetType(); type != null; type = type.BaseType)
                {
                    if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static Type? FindType(string fullName)
        {
            var type = Type.GetType(fullName);
            if (type != null)
            {
                return type;
            }

            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Select(assembly =>
                {
                    try
                    {
                        return assembly.GetType(fullName);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                })
                .FirstOrDefault(t => t != null);
        }
    }
}
