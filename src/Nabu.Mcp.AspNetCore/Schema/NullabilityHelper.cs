using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Nabu.Mcp.AspNetCore.Schema
{
    /// <summary>
    /// Reads the compiler-emitted nullable reference type metadata so that <c>string</c> and
    /// <c>string?</c> can be told apart. Implemented by inspecting attributes by name, which keeps it
    /// working on netstandard2.0 where <c>NullabilityInfoContext</c> does not exist.
    /// </summary>
    internal static class NullabilityHelper
    {
        private const string NullableAttributeName = "System.Runtime.CompilerServices.NullableAttribute";
        private const string NullableContextAttributeName = "System.Runtime.CompilerServices.NullableContextAttribute";

        /// <summary>
        /// Returns <c>true</c> when the member is definitely nullable, <c>false</c> when it is
        /// definitely non-nullable, and <c>null</c> when the compiler emitted no information.
        /// </summary>
        public static bool? IsNullable(ParameterInfo parameter)
        {
            if (parameter.ParameterType.IsValueType)
            {
                return Nullable.GetUnderlyingType(parameter.ParameterType) != null;
            }

            var fromParameter = ReadNullableFlag(parameter.GetCustomAttributesData());
            if (fromParameter.HasValue)
            {
                return fromParameter;
            }

            return ReadContextFlag(parameter.Member);
        }

        public static bool? IsNullable(PropertyInfo property)
        {
            if (property.PropertyType.IsValueType)
            {
                return Nullable.GetUnderlyingType(property.PropertyType) != null;
            }

            var fromProperty = ReadNullableFlag(property.GetCustomAttributesData());
            if (fromProperty.HasValue)
            {
                return fromProperty;
            }

            return ReadContextFlag(property.DeclaringType);
        }

        private static bool? ReadContextFlag(MemberInfo? member)
        {
            while (member != null)
            {
                var flag = ReadContextValue(member.GetCustomAttributesData());
                if (flag.HasValue)
                {
                    return flag;
                }

                member = member.DeclaringType;
            }

            return null;
        }

        private static bool? ReadNullableFlag(IEnumerable<CustomAttributeData> attributes)
        {
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeType.FullName != NullableAttributeName || attribute.ConstructorArguments.Count != 1)
                {
                    continue;
                }

                var argument = attribute.ConstructorArguments[0];
                if (argument.ArgumentType == typeof(byte) && argument.Value is byte flag)
                {
                    return flag == 2;
                }

                if (argument.Value is IReadOnlyCollection<CustomAttributeTypedArgument> flags && flags.Count > 0)
                {
                    var first = flags.First().Value;
                    return first is byte b && b == 2;
                }
            }

            return null;
        }

        private static bool? ReadContextValue(IEnumerable<CustomAttributeData> attributes)
        {
            foreach (var attribute in attributes)
            {
                if (attribute.AttributeType.FullName == NullableContextAttributeName &&
                    attribute.ConstructorArguments.Count == 1 &&
                    attribute.ConstructorArguments[0].Value is byte flag)
                {
                    return flag == 2;
                }
            }

            return null;
        }
    }
}
