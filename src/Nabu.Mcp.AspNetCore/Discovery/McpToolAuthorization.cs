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

        /// <summary>An action that demands nothing of its own.</summary>
        public static readonly McpToolAuthorization None = new McpToolAuthorization(false, NoAuthorizeData, NoPolicies);

        public McpToolAuthorization(
            bool allowsAnonymous,
            IReadOnlyList<IAuthorizeData>? authorizeData,
            IReadOnlyList<AuthorizationPolicy>? policies)
        {
            AllowsAnonymous = allowsAnonymous;
            AuthorizeData = authorizeData ?? NoAuthorizeData;
            Policies = policies ?? NoPolicies;

            HasAuthorizationMetadata = AuthorizeData.Count > 0 || Policies.Count > 0;
            RequiresAuthorization = !allowsAnonymous && HasAuthorizationMetadata;
        }

        /// <summary>
        /// True when <c>[AllowAnonymous]</c> (or an <c>IAllowAnonymousFilter</c>) applies to the action.
        /// As in MVC, it wins over every <c>[Authorize]</c> that also applies, and over the application's
        /// fallback policy.
        /// </summary>
        public bool AllowsAnonymous { get; }

        /// <summary>
        /// The <c>[Authorize]</c> metadata found on the action, its controller and the filter
        /// collection. Combined into a single policy when the caller is evaluated.
        /// </summary>
        public IReadOnlyList<IAuthorizeData> AuthorizeData { get; }

        /// <summary>
        /// Pre-built policies contributed by authorization filters that were registered with a policy
        /// instance rather than with a policy name.
        /// </summary>
        public IReadOnlyList<AuthorizationPolicy> Policies { get; }

        /// <summary>
        /// Whether the action carries any <c>[Authorize]</c> metadata of its own. An action with none is
        /// not necessarily anonymous: the application's fallback policy applies to it, which is why the
        /// final say belongs to <see cref="Server.IMcpToolAuthorizationEvaluator"/> rather than here.
        /// </summary>
        public bool HasAuthorizationMetadata { get; }

        /// <summary>
        /// Whether the action's own metadata demands authorization.
        /// </summary>
        /// <remarks>
        /// Reads the action in isolation, so it stays <c>false</c> for an action that carries nothing and
        /// is protected only by <c>AuthorizationOptions.FallbackPolicy</c>. Ask
        /// <see cref="Server.IMcpToolAuthorizationEvaluator.RequiresAuthorizationAsync"/> for the answer
        /// that accounts for the application's configuration as well.
        /// </remarks>
        public bool RequiresAuthorization { get; }
    }
}
