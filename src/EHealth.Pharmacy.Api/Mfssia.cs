using System.Text.Json;

namespace EHealth.Pharmacy;

// Location of the mfssia-ehealth gateway that fronts the DKG node. The demo stack
// sets MfssiaUrl per service in docker-compose; the default is the compose hostname
// so the service also works when run without explicit configuration.
public static class Mfssia
{
    public const string UrlConfigKey = "MfssiaUrl";
    public const string DefaultBaseUrl = "http://mfssia-ehealth:4000/api";

    public static string BaseUrl(IConfiguration config) =>
        config[UrlConfigKey] ?? DefaultBaseUrl;

    // Every mfssia payload arrives wrapped as { success, message, data: {...} }. Returns the
    // data object, or false when the response is shaped otherwise — an error envelope, or
    // anything else a proxy may have substituted for the service.
    public static bool TryUnwrap(JsonElement response, out JsonElement data)
    {
        if (response.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Object)
            return true;

        data = default;
        return false;
    }
}
