using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace rNotice.Probe;

/// <summary>
/// Token acquisition through the Windows broker (WAM).
///
/// Going through the broker means sign-in follows the same path Outlook takes, so Conditional
/// Access, device compliance and Windows Hello all still apply. The redirect URI it uses lives
/// in the app registration, not here — MSAL does not need it configured in code.
/// </summary>
internal sealed class Auth
{
    private readonly IPublicClientApplication _app;

    public Auth()
    {
        _app = PublicClientApplicationBuilder
            .Create(AppConfig.ClientId)
            .WithAuthority(AppConfig.Authority)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .WithDefaultRedirectUri()
            .Build();
    }

    /// <summary>
    /// Gets a token for exactly these scopes, silently when possible.
    ///
    /// Silent first against any cached account, then the signed-in Windows account, then an
    /// interactive prompt. The broker usually satisfies the second, so a normal run shows no UI
    /// at all after the first consent.
    /// </summary>
    public async Task<AuthResult> GetTokenAsync(string[] scopes, CancellationToken ct)
    {
        try
        {
            var account = (await _app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault()
                          ?? PublicClientApplication.OperatingSystemAccount;

            var silent = await _app.AcquireTokenSilent(scopes, account)
                                   .ExecuteAsync(ct).ConfigureAwait(false);

            return AuthResult.Ok(silent, interactive: false);
        }
        catch (MsalUiRequiredException)
        {
            // Expected on a first run, and whenever a scope has not been consented yet.
        }
        catch (Exception ex)
        {
            return AuthResult.Fail(Describe(ex));
        }

        try
        {
            var interactive = await _app.AcquireTokenInteractive(scopes)
                                        .WithParentActivityOrWindow(NativeMethods.GetConsoleWindowHandle())
                                        .ExecuteAsync(ct).ConfigureAwait(false);

            return AuthResult.Ok(interactive, interactive: true);
        }
        catch (Exception ex)
        {
            return AuthResult.Fail(Describe(ex));
        }
    }

    /// <summary>
    /// Turns an MSAL failure into something a person can act on. The raw messages are long and
    /// bury the error code, which is the only part that says what to do next.
    /// </summary>
    private static string Describe(Exception ex) => ex switch
    {
        MsalServiceException s when s.ErrorCode == "invalid_grant" || s.Message.Contains("AADSTS65001")
            => "Not consented. An administrator has not granted this permission.",
        MsalServiceException s when s.Message.Contains("AADSTS650057")
            => "Scope not valid for this application. Check it is listed in the app registration.",
        MsalServiceException s when s.Message.Contains("AADSTS900971") || s.Message.Contains("redirect")
            => "Redirect URI mismatch. The registration needs ms-appx-web://Microsoft.AAD.BrokerPlugin/<client-id>.",
        MsalServiceException s when s.Message.Contains("AADSTS50105")
            => "Sign-in blocked: your account is not assigned to this application.",
        MsalClientException c when c.ErrorCode == "broker_application_required" || c.Message.Contains("broker")
            => "The Windows broker could not be reached. Windows 10 1809 or later is required.",
        MsalServiceException s => $"{s.ErrorCode}: {FirstLine(s.Message)}",
        _ => $"{ex.GetType().Name}: {FirstLine(ex.Message)}"
    };

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? message;
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}

internal sealed record AuthResult(
    bool Succeeded,
    string? AccessToken,
    string? Upn,
    string[] GrantedScopes,
    bool WasInteractive,
    string? Error)
{
    public static AuthResult Ok(AuthenticationResult r, bool interactive) => new(
        true, r.AccessToken, r.Account?.Username, r.Scopes.ToArray(), interactive, null);

    public static AuthResult Fail(string error) => new(
        false, null, null, Array.Empty<string>(), false, error);
}
