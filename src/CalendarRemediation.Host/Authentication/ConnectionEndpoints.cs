using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace CalendarRemediation.Host.Authentication;

public static partial class ConnectionEndpoints
{
    public const string Scheme = "LocalConnection";

    public static IServiceCollection AddLocalConnection(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LocalConnectionOptions>(configuration.GetSection("LocalConnection"));
        services.AddSingleton<ILocalIdentityClient, LocalIdentityClient>();
        services.AddSingleton<ILocalCalendarClient, LocalCalendarClient>();
        services.AddSingleton<LocalConnectionService>();
        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = "CalendarRemediation.Connection";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            // The local app is deliberately HTTP loopback only.
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = context =>
                {
                    if (context.Principal is null || !context.HttpContext.RequestServices.GetRequiredService<LocalConnectionService>().IsCurrent(context.Principal))
                    {
                        context.RejectPrincipal();
                    }
                    return Task.CompletedTask;
                },
                OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; },
                OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; }
            };
        });
        services.AddAuthorization();
        services.AddCascadingAuthenticationState();
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        return services;
    }

    public static IApplicationBuilder UseLocalConnectionGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var options = context.RequestServices.GetRequiredService<IOptions<LocalConnectionOptions>>().Value;
        if (!IsAllowedLocalRequest(context.Request, context.Connection.RemoteIpAddress, options.Port))
        {
            RequestRejected(Logger(context), "local-request", "local-request-rejected", "None");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var request = context.Request;
        if (request.Path.StartsWithSegments("/connection") || request.Path == "/" || request.Path == "/connect")
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            // Native same-origin form posts need a non-null Origin for the local request guard.
            context.Response.Headers["Referrer-Policy"] = "same-origin";
        }
        await next(context);
    });

    public static IEndpointRouteBuilder MapLocalConnection(this IEndpointRouteBuilder app)
    {
        app.MapGet("/connection/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            try
            {
                var tokens = antiforgery.GetAndStoreTokens(context);
                return Results.Ok(new { tokens.RequestToken, tokens.FormFieldName, tokens.HeaderName });
            }
            catch (Exception ex)
            {
                RequestFailed(Logger(context), "antiforgery", "antiforgery-unavailable", ex.GetType().Name);
                throw;
            }
        });
        app.MapGet("/connection/state", async (HttpContext context, LocalConnectionService connection) =>
        {
            try { return Results.Ok(await connection.GetStateAsync(context.User, context.RequestAborted)); }
            catch (ConnectionException ex) { return Results.Json(ex.Problem, statusCode: 409); }
        });
        MapAction(app, "bootstrap", async (context, connection, _) =>
        {
            await IssueCookieAsync(context, connection.Bootstrap(context.User));
        });
        MapAction(app, "signin", BrowserSignInAsync);
        MapAction(app, "start", async (context, connection, form) =>
        {
            var mode = form["mode"].ToString();
            if (mode is not ("interactive" or "continue"))
            {
                throw new ConnectionException(new("invalid-mode", "Choose browser sign-in or continue with the cached account."));
            }
            if (mode == "interactive") { return await BrowserSignInAsync(context, connection, form); }
            await IssueCookieAsync(context, connection.StartSignIn(context.User, form["version"].ToString()));
            return Results.LocalRedirect("/connect");
        });
        MapAction(app, "complete", async (context, connection, form) =>
        {
            await IssueCookieAsync(context, connection.Complete(context.User, form["version"].ToString()));
        });
        MapAction(app, "cancel", (context, connection, form) =>
        {
            connection.Cancel(context.User, form["version"].ToString());
            return Task.CompletedTask;
        });
        MapAction(app, "location", (context, connection, form) =>
        {
            connection.ConfirmLocation(context.User, form["version"].ToString(),
                new(form["province"].ToString(), form["community"].ToString(), form["timeZoneId"].ToString()));
            return Task.CompletedTask;
        });
        MapAction(app, "discover", (context, connection, form) =>
        {
            connection.StartDiscovery(context.User, form["version"].ToString());
            return Task.CompletedTask;
        });
        MapAction(app, "preview", (context, connection, form) =>
        {
            var version = form["version"].ToString();
            connection.ClearPreview(context.User, version);
            var mode = form["mode"].ToString();
            if (mode is not ("local" or "instant"))
            {
                throw new ConnectionException(new("invalid-mode", "Choose a local time or keep the current instant."));
            }
            var localStart = default(DateTime);
            if (mode == "local" && !DateTime.TryParseExact(form["localStart"].ToString(),
                ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out localStart))
            {
                throw new ConnectionException(new("invalid-time", "Enter a local date and time in the meeting preview form."));
            }
            connection.PreviewMeeting(context.User, version, form["eventId"].ToString(), localStart,
                form["timeZoneId"].ToString(), mode == "instant");
            return Task.CompletedTask;
        });
        MapAction(app, "signout", async (context, connection, form) =>
        {
            try
            {
                // Once accepted, clearing the local cache must survive a disconnected browser.
                await connection.SignOutAsync(context.User, form["version"].ToString());
            }
            catch (ConnectionException ex) when (ex.Problem.Code == "stale-session") { throw; }
            catch
            {
                await context.SignOutAsync(Scheme);
                throw;
            }
            await context.SignOutAsync(Scheme);
        });
        return app;
    }

    private static void MapAction(IEndpointRouteBuilder app, string action,
        Func<HttpContext, LocalConnectionService, IFormCollection, Task> handler) =>
        MapAction(app, action, async (context, connection, form) =>
        {
            await handler(context, connection, form);
            return Results.LocalRedirect("/connect");
        });

    private static void MapAction(IEndpointRouteBuilder app, string action,
        Func<HttpContext, LocalConnectionService, IFormCollection, Task<IResult>> handler) =>
        app.MapPost("/connection/" + action, async (HttpContext context, IAntiforgery antiforgery, LocalConnectionService connection) =>
        {
            var logger = Logger(context);
            using var scope = logger.BeginScope(new Dictionary<string, object> { ["ConnectionRequestId"] = Guid.NewGuid() });
            try
            {
                if (!SameOrigin(context.Request.Headers.Origin.ToString(), context.Request)
                    && !SameOrigin(context.Request.Headers.Referer.ToString(), context.Request))
                {
                    RequestRejected(logger, action, "invalid-origin", "None");
                    return Results.Json(new ConnectionProblem("invalid-origin", "Open the connection page in this local browser."), statusCode: 403);
                }
                await antiforgery.ValidateRequestAsync(context);
                if (!context.Request.HasFormContentType || context.Request.ContentLength > 16 * 1024)
                {
                    RequestRejected(logger, action, "invalid-form", "None");
                    return Results.BadRequest(new ConnectionProblem("invalid-form", "Submit the connection form again."));
                }
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                return await handler(context, connection, form);
            }
            catch (AntiforgeryValidationException)
            {
                RequestRejected(logger, action, "antiforgery-rejected", nameof(AntiforgeryValidationException));
                return Results.BadRequest(new ConnectionProblem("invalid-form", "The form expired. Reload Connect and try again."));
            }
            catch (InvalidDataException)
            {
                RequestRejected(logger, action, "invalid-form", nameof(InvalidDataException));
                return Results.BadRequest(new ConnectionProblem("invalid-form", "The connection form was invalid. Reload Connect and try again."));
            }
            catch (ConnectionException ex)
            {
                RequestRejected(logger, action, LocalConnectionService.SafeReason(ex.Problem.Code), nameof(ConnectionException));
                return Results.Json(ex.Problem, statusCode: 409);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                RequestFailed(logger, action, "account-cache-unavailable", ex.GetType().Name);
                return Results.Json(new ConnectionProblem("account-cache-unavailable", "The account cache could not be cleared. Close other app instances and retry."), statusCode: 503);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                RequestRejected(logger, action, "operation-timeout-or-provider-cancelled", ex.GetType().Name);
                throw;
            }
            catch (Exception ex)
            {
                RequestFailed(logger, action, "unexpected-fault", ex.GetType().Name);
                throw;
            }
        });

    private static ILogger Logger(HttpContext context) => context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger(typeof(ConnectionEndpoints).FullName!);

    [LoggerMessage(7111, LogLevel.Warning, "Connection action {Action} rejected: {Reason}; exception type {ExceptionType}")]
    private static partial void RequestRejected(ILogger logger, string action, string reason, string exceptionType);

    [LoggerMessage(7112, LogLevel.Error, "Connection action {Action} faulted: {Reason}; exception type {ExceptionType}")]
    private static partial void RequestFailed(ILogger logger, string action, string reason, string exceptionType);

    private static async Task<IResult> BrowserSignInAsync(HttpContext context, LocalConnectionService connection, IFormCollection form)
    {
        // Request scheme, loopback Host and listener port have already passed the local request guard.
        var returnUri = new Uri($"{context.Request.Scheme}://{context.Request.Host}/connect");
        var ticket = connection.StartBrowserSignIn(context.User, form["version"].ToString(), returnUri);
        await IssueCookieAsync(context, ticket);
        var authorizationUri = await connection.WaitForBrowserSignInAsync(ticket, context.RequestAborted);
        return authorizationUri is null ? Results.LocalRedirect("/connect") : Results.Redirect(authorizationUri.AbsoluteUri);
    }

    private static Task IssueCookieAsync(HttpContext context, ConnectionTicket ticket)
    {
        return context.SignInAsync(Scheme, CreatePrincipal(ticket),
            new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
    }

    private static ClaimsPrincipal CreatePrincipal(ConnectionTicket ticket)
    {
        var claims = new List<Claim>
        {
            new(LocalConnectionService.SessionClaim, ticket.Id),
            new(ClaimTypes.NameIdentifier, ticket.Mailbox?.Identity.MailboxId ?? ticket.Id),
            new("connection_verified", ticket.Mailbox is null ? "false" : "true")
        };
        if (ticket.Mailbox is { } mailbox)
        {
            claims.Add(new(ClaimTypes.Name, mailbox.DisplayName));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    private static bool IsAllowedLocalRequest(HttpRequest request, IPAddress? remote, int configuredPort)
    {
        var host = request.Host.Host;
        return remote is not null && IPAddress.IsLoopback(remote) && request.Scheme == "http" && request.Host.Port == configuredPort
            && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "[::1]" or "::1")
            && (request.Headers.Origin.Count == 0 || SameOrigin(request.Headers.Origin.ToString(), request))
            // MSAL's localhost listener returns to Connect, which can be on a different loopback host.
            && (request.Headers["Sec-Fetch-Site"] != "cross-site"
                || (HttpMethods.IsGet(request.Method) && request.Path == "/connect"
                    && request.Headers["Sec-Fetch-Mode"] == "navigate" && request.Headers["Sec-Fetch-Dest"] == "document"));
    }

    private static bool SameOrigin(string value, HttpRequest request) => Uri.TryCreate(value, UriKind.Absolute, out var origin)
        && origin.Scheme == request.Scheme && origin.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase)
        && origin.UserInfo.Length == 0;
}
