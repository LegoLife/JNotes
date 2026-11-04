using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;

namespace JNotes.Web.Mcp;

/// <summary>Decides whether an MCP request may proceed. Swap for an OAuth implementation later.</summary>
public interface IMcpAuthenticator
{
    bool IsConfigured { get; }
    bool IsAuthorized(HttpContext context);
}

/// <summary>Static bearer token from JNotes:McpToken (LAN-only use with Claude Desktop + mcp-remote).</summary>
public sealed class StaticTokenAuthenticator(string? token) : IMcpAuthenticator
{
    private readonly byte[]? _expected = string.IsNullOrWhiteSpace(token) ? null : Encoding.UTF8.GetBytes(token.Trim());

    public bool IsConfigured => _expected is not null;

    public bool IsAuthorized(HttpContext context)
    {
        if (_expected is null) return false;
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(presented, _expected);
    }

    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class McpAuthMiddleware
{
    /// <summary>Guards /mcp: 503 when no token is configured, 401 without a valid bearer token.</summary>
    public static IApplicationBuilder UseMcpAuth(this IApplicationBuilder app) =>
        app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments("/mcp"), branch => branch.Use(async (ctx, next) =>
        {
            // MCP clients need real 401s, not the app's HTML status-code pages (which redirect to the login page).
            if (ctx.Features.Get<IStatusCodePagesFeature>() is { } pages) pages.Enabled = false;

            var auth = ctx.RequestServices.GetRequiredService<IMcpAuthenticator>();
            if (!auth.IsConfigured)
            {
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsync("MCP is disabled: set JNotes__McpToken.");
                return;
            }
            if (!auth.IsAuthorized(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next();
        }));
}
