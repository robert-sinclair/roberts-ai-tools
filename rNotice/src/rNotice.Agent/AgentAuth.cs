using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace rNotice.Agent;

public sealed class SignInRequiredException(string message) : Exception(message);

/// <summary>
/// Graph tokens through the Windows broker (WAM), as the probe does.
///
/// Polling only ever asks silently. If that fails the tray goes to a warning state and offers
/// "Sign in"; the agent never pops a sign-in window on its own.
/// </summary>
internal sealed class AgentAuth
{
    // Calendars.ReadWrite rather than .Read: it is the scope that was consented, and step 8
    // writes promoted events. This build only reads.
    private static readonly string[] Scopes =
    [
        "https://graph.microsoft.com/User.Read",
        "https://graph.microsoft.com/Calendars.ReadWrite",
        "https://graph.microsoft.com/Files.ReadWrite"
    ];

    private const string ClientId = "2923549f-f444-48f7-8084-c8b7799db2e0";
    private const string Tenant = "bravurasecurity.com";

    private readonly IPublicClientApplication _app;
    private readonly Func<IntPtr> _parentWindow;

    public AgentAuth(Func<IntPtr> parentWindow)
    {
        _parentWindow = parentWindow;
        _app = PublicClientApplicationBuilder
            .Create(Environment.GetEnvironmentVariable("RNOTICE_CLIENT_ID") ?? ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{Environment.GetEnvironmentVariable("RNOTICE_TENANT") ?? Tenant}")
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
            .WithDefaultRedirectUri()
            .Build();
    }

    public bool NeedsSignIn { get; private set; }
    public string? Upn { get; private set; }

    public event Action? StateChanged;

    /// <summary>Silent only. Throws <see cref="SignInRequiredException"/> when the user has to act.</summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        try
        {
            var account = (await _app.GetAccountsAsync().ConfigureAwait(true)).FirstOrDefault()
                          ?? PublicClientApplication.OperatingSystemAccount;
            var result = await _app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct).ConfigureAwait(true);
            SetState(false, result.Account?.Username);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            SetState(true, null);
            throw new SignInRequiredException(ex.Message);
        }
    }

    /// <summary>Only from an explicit user action — the tray's "Sign in".</summary>
    public async Task<bool> SignInAsync()
    {
        try
        {
            var result = await _app.AcquireTokenInteractive(Scopes)
                .WithParentActivityOrWindow(_parentWindow())
                .ExecuteAsync().ConfigureAwait(true);
            SetState(false, result.Account?.Username);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Interactive sign-in failed", ex);
            return false;
        }
    }

    private void SetState(bool needsSignIn, string? upn)
    {
        var changed = needsSignIn != NeedsSignIn || (upn is not null && upn != Upn);
        NeedsSignIn = needsSignIn;
        if (upn is not null) Upn = upn;
        if (changed) StateChanged?.Invoke();
    }
}
