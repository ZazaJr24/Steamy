using System.Globalization;

namespace Steamy.Services;

/// <summary>Public relay address only. Provider credentials belong exclusively on the VPS.</summary>
public static class ProviderRelay
{
    public static string BaseUrl { get; } = "https://steamy.213-171-214-218.sslip.io";
    public static string HealthUrl => BaseUrl + "/v1/health";

    public static HttpRequestMessage CreateRequest(string provider, int appId, string operation)
        => new(HttpMethod.Get, Url(provider, appId, operation));

    public static string Url(string provider, int appId, string operation)
    {
        if (provider is not ("ryuu" or "depotbox")) throw new ArgumentOutOfRangeException(nameof(provider));
        if (appId <= 0) throw new ArgumentOutOfRangeException(nameof(appId));
        if (operation is not ("archive" or "lua" or "availability")) throw new ArgumentOutOfRangeException(nameof(operation));
        return $"{BaseUrl}/v1/{provider}/{appId.ToString(CultureInfo.InvariantCulture)}/{operation}";
    }
}
