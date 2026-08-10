using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nabu.Mcp.AspNetCore.Discovery;

namespace Nabu.Mcp.AspNetCore.Server
{
    /// <summary>
    /// Default <see cref="IMcpToolAuthorizationEvaluator"/>: reads the <c>[Authorize]</c> metadata the
    /// registry collected for the action and evaluates it against the current caller with the
    /// application's own policy provider and authorization service.
    /// </summary>
    /// <remarks>
    /// The evaluation is deliberately one-sided. It can hide a tool from a caller, and it never lets one
    /// through: the tool call itself is authorized again by the application pipeline. When the answer
    /// cannot be worked out - authorization services are missing, or a scheme cannot be authenticated -
    /// the tool stays visible, so a misconfiguration produces a 403 from the action rather than a tool
    /// list that silently lost half its entries.
    /// </remarks>
    public class McpToolAuthorizationEvaluator : IMcpToolAuthorizationEvaluator
    {
        private readonly NabuMcpOptions _options;
        private readonly ILogger _logger;

        public McpToolAuthorizationEvaluator(
            IOptions<NabuMcpOptions> options,
            ILogger<McpToolAuthorizationEvaluator>? logger = null)
        {
            _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
            _logger = (ILogger?)logger ?? NullLogger.Instance;
        }

        public async Task<bool> IsVisibleAsync(McpToolDescriptor tool, HttpContext context, CancellationToken cancellationToken)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (_options.ToolVisibility == McpToolVisibility.All)
            {
                return true;
            }

            var authorization = tool.Authorization ?? McpToolAuthorization.None;
            if (!authorization.RequiresAuthorization)
            {
                return true;
            }

            var services = context.RequestServices;
            var policyProvider = services?.GetService(typeof(IAuthorizationPolicyProvider)) as IAuthorizationPolicyProvider;
            var authorizationService = services?.GetService(typeof(IAuthorizationService)) as IAuthorizationService;

            if (policyProvider == null || authorizationService == null)
            {
                _logger.LogWarning(
                    "Nabu MCP cannot filter the tool list for '{Tool}': ASP.NET Core authorization services are not " +
                    "registered. Call services.AddAuthorization() or set NabuMcpOptions.ToolVisibility back to All.",
                    tool.Name);
                return true;
            }

            AuthorizationPolicy? policy;
            try
            {
                policy = await ResolvePolicyAsync(authorization, policyProvider).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // A named policy the application does not define. The action would throw the same way,
                // so the tool is of no use to anyone; keep it out of the list.
                _logger.LogWarning(ex, "Nabu MCP hid the tool '{Tool}': its authorization policy could not be resolved.", tool.Name);
                return false;
            }

            if (policy == null)
            {
                return true;
            }

            var user = await ResolveUserAsync(context, policy, tool).ConfigureAwait(false);
            if (!IsAuthenticated(user))
            {
                return false;
            }

            if (_options.ToolVisibility == McpToolVisibility.Authenticated)
            {
                return true;
            }

            var result = await authorizationService.AuthorizeAsync(user, context, policy.Requirements).ConfigureAwait(false);
            return result.Succeeded;
        }

        /// <summary>
        /// Combines everything the action demands into one policy: the <c>[Authorize]</c> metadata, plus
        /// any policy instance contributed by a filter. An action forced to be protected through
        /// <see cref="McpToolAttribute.RequiresAuthorization"/> without carrying metadata of its own is
        /// measured against the application's default policy.
        /// </summary>
        protected virtual async Task<AuthorizationPolicy?> ResolvePolicyAsync(
            McpToolAuthorization authorization,
            IAuthorizationPolicyProvider policyProvider)
        {
            AuthorizationPolicy? policy = null;

            if (authorization.AuthorizeData.Count > 0)
            {
                policy = await AuthorizationPolicy.CombineAsync(policyProvider, authorization.AuthorizeData).ConfigureAwait(false);
            }

            if (authorization.Policies.Count > 0)
            {
                var builder = policy == null ? new AuthorizationPolicyBuilder() : new AuthorizationPolicyBuilder(policy);
                foreach (var contributed in authorization.Policies)
                {
                    builder.Combine(contributed);
                }

                policy = builder.Build();
            }

            return policy ?? await policyProvider.GetDefaultPolicyAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// The principal the tool would be invoked with. A policy naming its own authentication schemes
        /// is measured against those schemes rather than against whoever the MCP endpoint authenticated,
        /// which is what <c>AuthorizationMiddleware</c> does for a real request.
        /// </summary>
        private async Task<ClaimsPrincipal> ResolveUserAsync(HttpContext context, AuthorizationPolicy policy, McpToolDescriptor tool)
        {
            if (policy.AuthenticationSchemes.Count == 0)
            {
                return context.User ?? new ClaimsPrincipal(new ClaimsIdentity());
            }

            ClaimsPrincipal? principal = null;

            foreach (var scheme in policy.AuthenticationSchemes)
            {
                AuthenticateResult? result;
                try
                {
                    result = await context.AuthenticateAsync(scheme).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(
                        ex,
                        "Nabu MCP could not authenticate scheme '{Scheme}' while deciding whether to advertise '{Tool}'.",
                        scheme,
                        tool.Name);
                    continue;
                }

                if (result?.Succeeded == true && result.Principal != null)
                {
                    principal = Merge(principal, result.Principal);
                }
            }

            return principal ?? new ClaimsPrincipal(new ClaimsIdentity());
        }

        private static ClaimsPrincipal Merge(ClaimsPrincipal? existing, ClaimsPrincipal added)
        {
            if (existing == null)
            {
                return added;
            }

            var merged = new ClaimsPrincipal();
            merged.AddIdentities(existing.Identities);
            merged.AddIdentities(added.Identities);
            return merged;
        }

        private static bool IsAuthenticated(ClaimsPrincipal user)
        {
            foreach (var identity in user.Identities)
            {
                if (identity.IsAuthenticated)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
