﻿#if NETSTANDARD2_0 || NETSTANDARD1_6 || NET451
using Audit.Core;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using Microsoft.AspNetCore.Http.Extensions;
using Audit.Core.Extensions;

namespace Audit.WebApi
{
    /// <summary>
    /// Middleware to audit requests
    /// </summary>
    public class AuditMiddleware
    {
        private readonly RequestDelegate _next;

        private ConfigurationApi.AuditMiddlewareConfigurator _config;

        public AuditMiddleware(RequestDelegate next, ConfigurationApi.AuditMiddlewareConfigurator config)
        {
            _next = next;
            _config = config ?? new ConfigurationApi.AuditMiddlewareConfigurator();
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (Configuration.AuditDisabled)
            {
                await _next.Invoke(context);
                return;
            }
            var includeHeaders = _config._includeHeadersBuilder != null ? _config._includeHeadersBuilder.Invoke(context) : false;
            var includeRequest = _config._includeRequestBodyBuilder != null ? _config._includeRequestBodyBuilder.Invoke(context) : false;
            var eventTypeName = _config._eventTypeNameBuilder?.Invoke(context);
            var includeResponse = _config._includeResponseBodyBuilder != null ? _config._includeResponseBodyBuilder.Invoke(context) : false;
            var originalBody = context.Response.Body;

            // pre-filter
            if (_config._requestFilter != null && !_config._requestFilter.Invoke(context.Request))
            {
                await _next.Invoke(context);
                return;
            }

            await BeforeInvoke(context, includeHeaders, includeRequest, eventTypeName);

            if (includeResponse)
            {
                using (var responseBody = new MemoryStream())
                {
                    context.Response.Body = responseBody;
                    await InvokeNextAsync(context, true);
                    await responseBody.CopyToAsync(originalBody);
                }
            }
            else
            {
                await InvokeNextAsync(context, false);
            }
        }

        private async Task InvokeNextAsync(HttpContext context, bool includeResponseBody)
        {
            Exception exception = null;
            try
            {
                await _next.Invoke(context);
            }
            catch (Exception ex)
            {
                exception = ex;
                throw;
            }
            finally
            {
                await AfterInvoke(context, includeResponseBody, exception);
            }
        }

        private async Task BeforeInvoke(HttpContext context, bool includeHeaders, bool includeRequestBody, string eventTypeName)
        {
            var auditAction = new AuditApiAction
            {
                IsMiddleware = true,
                UserName = context.User?.Identity.Name,
                IpAddress = context.Connection?.RemoteIpAddress?.ToString(),
                RequestUrl = context.Request.GetDisplayUrl(),
                HttpMethod = context.Request.Method,
                FormVariables = AuditApiHelper.GetFormVariables(context),
                Headers = includeHeaders ? AuditApiHelper.ToDictionary(context.Request.Headers) : null,
                ActionName = null,
                ControllerName = null,
                ActionParameters = null,
                RequestBody = new BodyContent
                {
                    Type = context.Request.ContentType,
                    Length = context.Request.ContentLength,
                    Value = includeRequestBody ? AuditApiHelper.GetRequestBody(context) : null
                },
                TraceId = context.TraceIdentifier
            };
            var eventType = (eventTypeName ?? "{verb} {url}").Replace("{verb}", auditAction.HttpMethod)
                .Replace("{url}", auditAction.RequestUrl);
            // Create the audit scope
            var auditEventAction = new AuditEventWebApi()
            {
                Action = auditAction
            };
            var auditScope = await AuditScope.CreateAsync(new AuditScopeOptions() { EventType = eventType, AuditEvent = auditEventAction });
            context.Items[AuditApiHelper.AuditApiActionKey] = auditAction;
            context.Items[AuditApiHelper.AuditApiScopeKey] = auditScope;
        }

        private async Task AfterInvoke(HttpContext context, bool includeResponseBody, Exception exception)
        {
            var auditAction = context.Items[AuditApiHelper.AuditApiActionKey] as AuditApiAction;
            var auditScope = context.Items[AuditApiHelper.AuditApiScopeKey] as AuditScope;
           
            if (auditAction != null && auditScope != null)
            {
                if (exception != null)
                {
                    auditAction.Exception = exception.GetExceptionInfo();
                    auditAction.ResponseStatusCode = 500;
                    auditAction.ResponseStatus = "Internal Server Error";
                }
                else if (context.Response != null)
                {
                    var statusCode = context.Response.StatusCode;
                    auditAction.ResponseStatusCode = statusCode;
                    auditAction.ResponseStatus = AuditApiHelper.GetStatusCodeString(statusCode);
                    if (includeResponseBody && auditAction.ResponseBody == null)
                    {
                        auditAction.ResponseBody = new BodyContent
                        {
                            Type = context.Response.ContentType,
                            Length = context.Response.ContentLength,
                            Value = AuditApiHelper.GetResponseBody(context)
                        };
                    }
                }
                // Replace the Action field and save
                (auditScope.Event as AuditEventWebApi).Action = auditAction;
                await auditScope.SaveAsync();
            }
        }

    }
}
#endif