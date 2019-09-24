// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Authorization
{
    public class AuthorizationMiddleware
    {
        // Property key is used by other systems, e.g. MVC, to check if authorization middleware has run
        private const string AuthorizationMiddlewareInvokedKey = "__AuthorizationMiddlewareInvoked";
        private static readonly object AuthorizationMiddlewareInvokedValue = new object();

        private readonly RequestDelegate _next;
        private readonly IAuthorizationPolicyProvider _policyProvider;

        public AuthorizationMiddleware(RequestDelegate next, IAuthorizationPolicyProvider policyProvider)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        }

        public async Task Invoke(HttpContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var endpoint = context.GetEndpoint();

            if (endpoint != null)
            {
                // Flag to indicate to the system that the middleware was run in the context of endpoint routing.
                // Setting this flag allows a check in EndpointRoutingMiddleware that verifies if the middleware
                // pipeline is wired correctly to succeed.
                context.Items[AuthorizationMiddlewareInvokedKey] = AuthorizationMiddlewareInvokedValue;
            }

            // IMPORTANT: Changes to authorization logic should be mirrored in MVC's AuthorizeFilter
            var authorizeData = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>() ?? Array.Empty<IAuthorizeData>();
            var policy = await AuthorizationPolicy.CombineAsync(_policyProvider, authorizeData);
            if (policy == null)
            {
                await _next(context);
                return;
            }

            // Policy evaluator has transient lifetime so it fetched from request services instead of injecting in constructor
            var policyEvaluator = context.RequestServices.GetRequiredService<IPolicyEvaluator>();

            var authenticateResult = await policyEvaluator.AuthenticateAsync(policy, context);

            // Allow Anonymous skips all authorization
            if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() != null)
            {
                await _next(context);
                return;
            }

            // Note that the resource will be null if there is no matched endpoint
            var authorizeResult = await policyEvaluator.AuthorizeAsync(policy, authenticateResult, context, resource: endpoint);

            if (authorizeResult.Challenged)
            {
                if (policy.AuthenticationSchemes.Any())
                {
                    foreach (var scheme in policy.AuthenticationSchemes)
                    {
                        await context.ChallengeAsync(scheme);
                    }
                }
                else
                {
                    await context.ChallengeAsync();
                }

                return;
            }
            else if (authorizeResult.Forbidden)
            {
                if (policy.AuthenticationSchemes.Any())
                {
                    foreach (var scheme in policy.AuthenticationSchemes)
                    {
                        await context.ForbidAsync(scheme);
                    }
                }
                else
                {
                    await context.ForbidAsync();
                }

                return;
            }

            await _next(context);
        }
    }
}
