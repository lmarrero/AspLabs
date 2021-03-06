// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebHooks.Properties;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.WebHooks.Filters
{
    /// <summary>
    /// An <see cref="IAsyncResourceFilter"/> that verifies the Pusher signature header. Confirms the header exists,
    /// reads Body bytes, and compares the hashes.
    /// </summary>
    public class PusherVerifySignatureFilter : WebHookVerifySignatureFilter, IAsyncResourceFilter
    {
        /// <summary>
        /// Instantiates a new <see cref="PusherVerifySignatureFilter"/> instance.
        /// </summary>
        /// <param name="configuration">
        /// The <see cref="IConfiguration"/> used to initialize <see cref="WebHookSecurityFilter.Configuration"/>.
        /// </param>
        /// <param name="hostingEnvironment">
        /// The <see cref="IHostingEnvironment" /> used to initialize
        /// <see cref="WebHookSecurityFilter.HostingEnvironment"/>.
        /// </param>
        /// <param name="loggerFactory">
        /// The <see cref="ILoggerFactory"/> used to initialize <see cref="WebHookSecurityFilter.Logger"/>.
        /// </param>
        public PusherVerifySignatureFilter(
            IConfiguration configuration,
            IHostingEnvironment hostingEnvironment,
            ILoggerFactory loggerFactory)
            : base(configuration, hostingEnvironment, loggerFactory)
        {
        }

        /// <inheritdoc />
        public override string ReceiverName => PusherConstants.ReceiverName;

        /// <inheritdoc />
        public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            if (next == null)
            {
                throw new ArgumentNullException(nameof(next));
            }

            var request = context.HttpContext.Request;
            if (HttpMethods.IsPost(request.Method))
            {
                // 1. Confirm a secure connection.
                var errorResult = EnsureSecureConnection(ReceiverName, context.HttpContext.Request);
                if (errorResult != null)
                {
                    context.Result = errorResult;
                    return;
                }

                // 2. Get the expected hash from the signature headers.
                var header = GetRequestHeader(request, PusherConstants.SignatureHeaderName, out errorResult);
                if (errorResult != null)
                {
                    context.Result = errorResult;
                    return;
                }

                var expectedHash = FromHex(header, PusherConstants.SignatureHeaderName);
                if (expectedHash == null)
                {
                    context.Result = CreateBadHexEncodingResult(PusherConstants.SignatureHeaderName);
                    return;
                }

                // 3. Get the configured secret key.
                var secretKeys = GetSecretKeys(ReceiverName, context.RouteData);
                if (!secretKeys.Exists())
                {
                    context.Result = new NotFoundResult();
                    return;
                }

                var applicationKey = GetRequestHeader(
                    request,
                    PusherConstants.SignatureKeyHeaderName,
                    out errorResult);
                if (errorResult != null)
                {
                    context.Result = errorResult;
                    return;
                }

                var secretKey = secretKeys[applicationKey];
                if (secretKey == null || secretKey.Length < PusherConstants.SecretKeyMinLength)
                {
                    Logger.LogWarning(
                        0,
                        $"The '{PusherConstants.SignatureKeyHeaderName}' header value of '{{HeaderValue}}' is not " +
                        "recognized as a valid application key. Ensure the correct application key / secret key " +
                        "pairs have been configured.",
                        applicationKey);

                    var message = string.Format(
                        CultureInfo.CurrentCulture,
                        Resources.SignatureFilter_SecretNotFound,
                        PusherConstants.SignatureKeyHeaderName,
                        applicationKey);

                    context.Result = new BadRequestObjectResult(message);
                    return;
                }

                var secret = Encoding.UTF8.GetBytes(secretKey);

                // 4. Get the actual hash of the request body.
                var actualHash = await ComputeRequestBodySha256HashAsync(request, secret);

                // 5. Verify that the actual hash matches the expected hash.
                if (!SecretEqual(expectedHash, actualHash))
                {
                    // Log about the issue and short-circuit remainder of the pipeline.
                    errorResult = CreateBadSignatureResult(PusherConstants.SignatureHeaderName);

                    context.Result = errorResult;
                    return;
                }
            }

            await next();
        }
    }
}
