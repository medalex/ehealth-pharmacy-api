namespace EHealth.Pharmacy;

// The patient service owns consent. Its URL is set per environment via the PatientApiUrl
// configuration key; the default is the compose hostname of the demo stack.
public static class PatientApi
{
    public const string UrlConfigKey = "PatientApiUrl";
    public const string DefaultBaseUrl = "http://patient-api:3001";

    public static string BaseUrl(IConfiguration config) =>
        config[UrlConfigKey] ?? DefaultBaseUrl;

    // Whether the patient has an active consent covering this organisation. An unreachable
    // patient service means no consent can be shown, so it denies rather than assumes.
    public static async Task<bool> ConsentGranted(
        Guid patientId, string organizationId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var client = http.CreateClient();
            var response = await client.GetAsync(
                $"{BaseUrl(config)}/api/consents/check?patientId={patientId}&organizationId={organizationId}");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
