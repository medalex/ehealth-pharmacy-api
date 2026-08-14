using System.Net;
using System.Net.Http.Json;
using EHealth.Pharmacy.Models;
using EHealth.Pharmacy.Api.Tests.Helpers;

namespace EHealth.Pharmacy.Api.Tests;

public class PharmacyEndpointsTests : IDisposable
{
    private readonly TestFactory _factory = new();
    private readonly HttpClient _client;

    private static readonly Guid Pat1 = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public PharmacyEndpointsTests()
    {
        _client = _factory.CreateClient();
    }

    // Nothing is seeded — prescriptions arrive from the hospital during the demo — so every
    // test that needs one posts it first.
    private async Task<ReceivedPrescription> Receive(
        int drugId = 3, string drugName = "Ibuprofen", string stmtHash = "0xtest001")
    {
        var response = await _client.PostAsJsonAsync("/api/prescriptions/receive", new
        {
            drugId,
            drugName,
            dosage = "400mg 3x/day",
            patientId = Pat1,
            stmtHash,
            proofJson = "{}",
            publicSignalsJson = "[]",
            outcome = true,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ReceivedPrescription>(TestFactory.Json))!;
    }

    [Fact]
    public async Task GetAll_EnsuresNoSeededRecords()
    {
        var response = await _client.GetAsync("/api/prescriptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ReceivedPrescription>>(TestFactory.Json);
        Assert.NotNull(list);
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetById_KnownId_ReturnsPrescription()
    {
        var created = await Receive();

        var response = await _client.GetAsync($"/api/prescriptions/{created.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var p = await response.Content.ReadFromJsonAsync<ReceivedPrescription>(TestFactory.Json);
        Assert.Equal(created.Id, p!.Id);
    }

    [Fact]
    public async Task GetById_UnknownId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/prescriptions/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Receive_CreatesPrescription_WithReceivedStatus()
    {
        var created = await Receive();

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal("Ibuprofen", created.DrugName);
        Assert.Equal(PrescriptionStatus.Received, created.Status);
        Assert.Null(created.VerifiedAt);
        Assert.Null(created.DispensedAt);
    }

    [Fact]
    public async Task Dispense_WithoutVerify_ReturnsBadRequest()
    {
        var created = await Receive(drugId: 4, drugName: "Aspirin", stmtHash: "0xtest002");

        var dispense = await _client.PostAsync($"/api/prescriptions/{created.Id}/dispense", null);

        Assert.Equal(HttpStatusCode.BadRequest, dispense.StatusCode);
    }

    [Fact]
    public async Task Dispense_VerifiedWithoutConsent_ReturnsForbidden()
    {
        // The consent gate runs after the Verified check and before the on-chain registry
        // write, so a verified prescription is still refused while consent is missing.
        var verified = new ReceivedPrescription
        {
            DrugId = 5,
            DrugName = "Amoxicillin",
            Dosage = "500mg 2x/day",
            PatientId = Pat1,
            StmtHash = "0xtest003",
            ProofJson = "{}",
            PublicSignalsJson = "[]",
            Outcome = true,
            Status = PrescriptionStatus.Verified,
            VerifiedAt = DateTime.UtcNow,
        };
        await _factory.SeedAsync(verified);

        var response = await _client.PostAsync($"/api/prescriptions/{verified.Id}/dispense", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Dispense_UnknownId_ReturnsNotFound()
    {
        var response = await _client.PostAsync($"/api/prescriptions/{Guid.NewGuid()}/dispense", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
