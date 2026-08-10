using System.Collections.Generic;
using Microsoft.AspNetCore.Authorization;

namespace Nabu.Mcp.AspNetCore.Discovery
{
    /// <summary>
    /// What the action behind a tool demands of its caller, read once during discovery so the tool list
    /// can be tailored to whoever is asking for it without invoking anything.
    /// </summary>
    /// <remarks>
    /// This is descriptive only. Authorization itself is always enforced by the application pipeline
    /// when the tool is invoked; nothing here shortcuts, caches or replaces it.
    /// </remarks>
    public sealed class McpToolAuthorization
    {
        private static readonly IAuthorizeData[] NoAuthorizeData = new IAuthorizeData[0];
        private static readonly AuthorizationPolicy[] NoPolicies = new AuthorizationPolicy[0];

        /// <summary>An action that demands nothing, and is therefore reachable anonymously.</summary>
        public static readonly McpToolAuthorization None =
            new McpToolAuthorization(false, NoAuthorizeData, NoPolicies, null);

        public McpToolAuthorization(
            bool allowsAnonymous,
            IReadOnlyList<IAuthorizeData>? authorizeData,
            IReadOnlyList<AuthorizationPolicy>? policies,
            bool? requiresAuthorizationOverride)
        {
            AllowsAnonymous = allowsAnonymous;
            AuthorizeData = authorizeData ?? NoAuthorizeData;
            Policies = policies ?? NoPolicies;
            RequiresAuthorizationOverride = requiresAuthorizationOverride;

            RequiresAuthorization = requiresAuthorizationOverride
                ?? (!allowsAnonymous && (AuthorizeData.Count > 0 || Policies.Count > 0));
        }

        /// <summary>
        /// True when <c>[AllowAnonymous]</c> (or an <c>IAllowAnonymousFilter</c>) applies to the action.
        /// As in MVC, it wins over every <c>[Authorize]</c> that also applies.
        /// </summary>
        public bool AllowsAnonymous { get; }

        /// <summary>
        /// The <c>[Authorize]</c> metadata found on the action, its controller and the global filter
        /// collection. Combined into a single policy when the caller is evaluated.
        /// </summary>
        public IReadOnlyList<IAuthorizeData> AuthorizeData { get; }

        /// <summary>
        /// Pre-built policies contributed by authorization filters that were registered with a policy
        /// instance rather than with a policy name.
        /// </summary>
        public IReadOnlyList<AuthorizationPolicy> Policies { get; }

        /// <summary>
        /// The value of <see cref="McpToolAttribute.RequiresAuthorization"/>, when the tool set one.
        /// </summary>
        public bool? RequiresAuthorizationOverride { get; }

        /// <summary>
        /// Whether a caller has to be authorized before the tool is worth advertising. Honours
        /// <see cref="RequiresAuthorizationOverride"/> when the tool declared one.
        /// </summary>
        public bool RequiresAuthorization { get; }
    }
}
