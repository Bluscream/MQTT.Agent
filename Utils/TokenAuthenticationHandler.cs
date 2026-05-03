using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MqttAgent.Utils;

public class TokenService
{
    public string Token { get; }
    public TokenService(string token) => Token = token;
}

public class TokenAuthenticationSchemeOptions : AuthenticationSchemeOptions { }

public class TokenAuthenticationHandler : AuthenticationHandler<TokenAuthenticationSchemeOptions>
{
    private readonly TokenService _tokenService;

    public TokenAuthenticationHandler(
        IOptionsMonitor<TokenAuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TokenService tokenService)
        : base(options, logger, encoder)
    {
        _tokenService = tokenService;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Method == "OPTIONS")
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (TryAuthenticateWithToken(out var principal))
        {
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        return Task.FromResult(AuthenticateResult.Fail("Invalid or missing token"));
    }

    private bool TryAuthenticateWithToken(out ClaimsPrincipal principal)
    {
        principal = null!;

        // Check query parameter
        if (Request.Query.TryGetValue("token", out var queryToken))
        {
            if (queryToken == _tokenService.Token)
            {
                principal = CreatePrincipal();
                return true;
            }
        }

        // Check Authorization header
        var authHeader = Request.Headers["Authorization"].FirstOrDefault();
        if (authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var providedToken = authHeader.Substring("Bearer ".Length).Trim();
            if (providedToken == _tokenService.Token)
            {
                principal = CreatePrincipal();
                return true;
            }
        }

        return false;
    }

    private ClaimsPrincipal CreatePrincipal()
    {
        var claims = new[] { new Claim(ClaimTypes.Name, "authenticated") };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return new ClaimsPrincipal(identity);
    }
}
