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

    [Fact]
    public async Task GetAll_ReturnsSeededRecords()
    {
        var response = await _client.GetAsync("/api/prescriptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<ReceivedPrescription>>();
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public async Task GetById_KnownId_ReturnsPrescription()
    {
        var all = await _client.GetFromJsonAsync<List<ReceivedPrescription>>("/api/prescriptions");
        var id = all![0].Id;

        var response = await _client.GetAsync($"/api/prescriptions/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var p = await response.Content.ReadFromJsonAsync<ReceivedPrescription>();
        Assert.Equal(id, p!.Id);
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
        var req = new
        {
            drugId = 3,
            drugName = "Ibuprofen",
            dosage = "400mg 3x/day",
            patientId = Pat1,
            stmtHash = "0xtest001",
            proofJson = "{}",
            publicSignalsJson = "[]",
            outcome = true
        };

        var response = await _client.PostAsJsonAsync("/api/prescriptions/receive", req);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<ReceivedPrescription>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Id);
        Assert.Equal("Ibuprofen", created.DrugName);
        Assert.Equal(PrescriptionStatus.Received, created.Status);
        Assert.Null(created.VerifiedAt);
        Assert.Null(created.DispensedAt);
    }

    [Fact]
    public async Task Dispense_WithoutVerify_ReturnsBadRequest()
    {
        var req = new
        {
            drugId = 4,
            drugName = "Aspirin",
            dosage = "100mg/day",
            patientId = Pat1,
            stmtHash = "0xtest002",
            proofJson = "{}",
            publicSignalsJson = "[]",
            outcome = true
        };

        var create = await _client.PostAsJsonAsync("/api/prescriptions/receive", req);
        var created = await create.Content.ReadFromJsonAsync<ReceivedPrescription>();

        var dispense = await _client.PostAsync($"/api/prescriptions/{created!.Id}/dispense", null);

        Assert.Equal(HttpStatusCode.BadRequest, dispense.StatusCode);
    }

    [Fact]
    public async Task Dispense_AlreadyVerifiedSeedRecord_ReturnsOk()
    {
        // Seeder plants a prescription with Status=Verified
        var all = await _client.GetFromJsonAsync<List<ReceivedPrescription>>("/api/prescriptions");
        var verified = all!.First(p => p.Status == PrescriptionStatus.Verified);

        var response = await _client.PostAsync($"/api/prescriptions/{verified.Id}/dispense", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Dispense_UnknownId_ReturnsNotFound()
    {
        var response = await _client.PostAsync($"/api/prescriptions/{Guid.NewGuid()}/dispense", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
