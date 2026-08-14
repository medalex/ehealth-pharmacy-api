using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using EHealth.Pharmacy.Data;
using EHealth.Pharmacy.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace EHealth.Pharmacy.Api.Tests.Helpers;

public class TestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    // The API serialises enums as strings (ConfigureHttpJsonOptions in Program.cs), which the
    // default deserialiser rejects, so responses must be read back with the same converter.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public TestFactory()
    {
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var toRemove = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>))
                .ToList();
            foreach (var d in toRemove)
                services.Remove(d);

            // Reuse the same open connection so :memory: DB persists across requests
            services.AddDbContext<AppDbContext>(opt =>
                opt.UseSqlite(_connection));

            // patient-api, mfssia and the decision registry are not part of these tests. Failing
            // the calls outright (rather than waiting on an unresolvable host) keeps them fast and
            // deterministic: each gate they guard then takes its documented deny path.
            services.ConfigureAll<HttpClientFactoryOptions>(o =>
                o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new UpstreamStub()));
        });
    }

    // Nothing is seeded, and /verify needs the on-chain verifier, so a prescription that has to
    // start out already verified is written straight to the database.
    public async Task SeedAsync(params ReceivedPrescription[] prescriptions)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Prescriptions.AddRange(prescriptions);
        await db.SaveChangesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}

internal sealed class UpstreamStub : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
}
