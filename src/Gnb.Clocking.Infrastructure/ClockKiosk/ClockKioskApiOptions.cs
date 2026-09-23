namespace Gnb.Clocking.Infrastructure.ClockKiosk;

/// <summary>
/// gnbSaasApi <c>api/clock-kiosk</c> connection. <c>ClockKiosk:BaseUrl</c> and <c>ClockKiosk:ApiKey</c>
/// come from configuration; the key is never committed.
/// </summary>
public sealed class ClockKioskApiOptions
{
    public const string SectionName = "ClockKiosk";
    public const string KeyHeaderName = "X-Clock-Kiosk-Key";

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    /// <summary>Display scope from <c>ClockKiosk__TenantIds</c>, for example <c>6</c> or <c>6,1</c>.</summary>
    public string? TenantIds { get; init; }

    /// <summary>Display scope from <c>ClockKiosk__OrganizationIds</c>.</summary>
    public string? OrganizationIds { get; init; }

    public string ScopeLine
    {
        get
        {
            var tenants = SplitIds(TenantIds);
            var organizations = SplitIds(OrganizationIds);
            if (tenants.Length == 0 && organizations.Length == 0)
                return "GroupNB clock";

            var tenantLabel = tenants.Length switch
            {
                0 => null,
                1 => $"Tenant {tenants[0]}",
                _ => $"Tenants {string.Join(", ", tenants)}",
            };
            var organizationLabel = organizations.Length switch
            {
                0 => null,
                1 => $"Organization {organizations[0]}",
                _ => $"Organizations {string.Join(", ", organizations)}",
            };
            return string.Join(" · ", new[] { tenantLabel, organizationLabel }.Where(part => part != null));
        }
    }

    private static string[] SplitIds(string? value) =>
        (value ?? string.Empty)
            .Split(new[] { ',', '[', ']', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Folder that holds the local copy of clock photos (<c>Candidates/{id}/Records/...</c>).</summary>
    public required string PhotoRoot { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiKey)
        && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
}
