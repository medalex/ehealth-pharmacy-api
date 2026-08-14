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
}
