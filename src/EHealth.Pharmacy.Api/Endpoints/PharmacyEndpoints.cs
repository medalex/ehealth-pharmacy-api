using EHealth.Pharmacy.Data;
using EHealth.Pharmacy.Models;
using Microsoft.EntityFrameworkCore;

namespace EHealth.Pharmacy.Endpoints;

public static class PharmacyEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/prescriptions").WithTags("Pharmacy");

        group.MapGet("/", async (AppDbContext db) =>
            await db.Prescriptions.OrderByDescending(p => p.ReceivedAt).ToListAsync());

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
            await db.Prescriptions.FindAsync(id) is { } p
                ? Results.Ok(p)
                : Results.NotFound());

        group.MapPost("/receive", async (ReceivePrescriptionRequest req, AppDbContext db) =>
        {
            var prescription = new ReceivedPrescription
            {
                Id = Guid.NewGuid(),
                DrugId = req.DrugId,
                DrugName = req.DrugName,
                Dosage = req.Dosage,
                PatientId = req.PatientId,
                StmtHash = req.StmtHash,
                ProofJson = req.ProofJson,
                PublicSignalsJson = req.PublicSignalsJson,
                Outcome = req.Outcome,
                Status = PrescriptionStatus.Received,
                ReceivedAt = DateTime.UtcNow
            };

            db.Prescriptions.Add(prescription);
            await db.SaveChangesAsync();

            return Results.Created($"/api/prescriptions/{prescription.Id}", prescription);
        });

        // Re-verify ZKP proof locally; sets status to Verified or Rejected
        group.MapPost("/{id:guid}/verify", async (
            Guid id, AppDbContext db,
            IHttpClientFactory http, IConfiguration config) =>
        {
            var prescription = await db.Prescriptions.FindAsync(id);
            if (prescription is null) return Results.NotFound();
            if (prescription.Status == PrescriptionStatus.Dispensed)
                return Results.BadRequest("Prescription already dispensed");

            var isValid = await VerifyWithZkpProver(prescription, http, config);

            prescription.Status = isValid ? PrescriptionStatus.Verified : PrescriptionStatus.Rejected;
            prescription.VerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { id, verified = isValid, status = prescription.Status.ToString() });
        });

        // Dispense: only allowed when status is Verified
        group.MapPost("/{id:guid}/dispense", async (Guid id, AppDbContext db) =>
        {
            var prescription = await db.Prescriptions.FindAsync(id);
            if (prescription is null) return Results.NotFound();
            if (prescription.Status != PrescriptionStatus.Verified)
                return Results.BadRequest("Prescription must be verified before dispensing");

            prescription.Status = PrescriptionStatus.Dispensed;
            prescription.DispensedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { id, dispensedAt = prescription.DispensedAt });
        });
    }

    private static async Task<bool> VerifyWithZkpProver(
        ReceivedPrescription p, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var zkpUrl = config["ZkpProverUrl"] ?? "http://zkp-prover:3005";
            var client = http.CreateClient();

            var res = await client.PostAsJsonAsync($"{zkpUrl}/verify", new
            {
                proof = p.ProofJson,
                publicSignals = p.PublicSignalsJson
            });

            if (!res.IsSuccessStatusCode) return false;
            var result = await res.Content.ReadFromJsonAsync<VerifyResult>();
            return result?.Valid ?? false;
        }
        catch { return false; }
    }

    private record ReceivePrescriptionRequest(
        int DrugId, string DrugName, string Dosage, Guid PatientId,
        string StmtHash, string ProofJson, string PublicSignalsJson, bool Outcome);

    private record VerifyResult(bool Valid);
}
