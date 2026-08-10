using System;
using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace Nabu.Mcp.AspNetCore.Tests.Integration
{
    /// <summary>
    /// Every test controller lives in this one assembly, so a test server taking the default controller
    /// feature would also discover the controllers written for other test classes - and assertions about
    /// "the whole tool list" would depend on which files exist. Each server declares its controllers.
    /// </summary>
    internal static class ControllerIsolation
    {
        public static IMvcBuilder OnlyControllers(this IMvcBuilder builder, params Type[] controllers)
        {
            return builder.ConfigureApplicationPartManager(manager =>
            {
                for (var i = manager.FeatureProviders.Count - 1; i >= 0; i--)
                {
                    if (manager.FeatureProviders[i] is ControllerFeatureProvider)
                    {
                        manager.FeatureProviders.RemoveAt(i);
                    }
                }

                manager.FeatureProviders.Add(new ExplicitControllerFeatureProvider(controllers));
            });
        }

        private sealed class ExplicitControllerFeatureProvider : ControllerFeatureProvider
        {
            private readonly Type[] _controllers;

            public ExplicitControllerFeatureProvider(Type[] controllers)
            {
                _controllers = controllers;
            }

            protected override bool IsController(TypeInfo typeInfo)
            {
                return Array.IndexOf(_controllers, typeInfo.AsType()) >= 0 && base.IsController(typeInfo);
            }
        }
    }
}
