namespace rNotice.Probe;

/// <summary>
/// Everything environment-specific, in one place.
///
/// The client ID is not a secret. A public client registration has no secret and no
/// certificate, and the client ID ships inside the binary by design — see IT-REQUEST.md.
/// Each value can still be overridden by an environment variable so this runs against a
/// different tenant or registration without a rebuild.
/// </summary>
internal static class AppConfig
{
    /// <summary>Application (client) ID of the rNotice registration in the Bravura Security tenant.</summary>
    public static string ClientId =>
        Environment.GetEnvironmentVariable("RNOTICE_CLIENT_ID")
        ?? "2923549f-f444-48f7-8084-c8b7799db2e0";

    /// <summary>Single-tenant authority. A verified domain works as the tenant identifier.</summary>
    public static string Tenant =>
        Environment.GetEnvironmentVariable("RNOTICE_TENANT")
        ?? "bravurasecurity.com";

    public static string Authority => $"https://login.microsoftonline.com/{Tenant}";

    public const string GraphBase = "https://graph.microsoft.com/v1.0";

    /// <summary>OneDrive folder holding the task workbook. Not touched by this probe beyond a listing.</summary>
    public static string WorkbookFolder =>
        Environment.GetEnvironmentVariable("RNOTICE_WORKBOOK_FOLDER") ?? "efficiency";
}
