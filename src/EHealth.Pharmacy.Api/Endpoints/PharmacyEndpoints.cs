using EHealth.Pharmacy.Data;
using EHealth.Pharmacy.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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

        // Dispense: only allowed when status is Verified and patient has given consent
        group.MapPost("/{id:guid}/dispense", async (
            Guid id, AppDbContext db,
            IHttpClientFactory http, IConfiguration config) =>
        {
            var prescription = await db.Prescriptions.FindAsync(id);
            if (prescription is null) return Results.NotFound();
            if (prescription.Status != PrescriptionStatus.Verified)
                return Results.BadRequest("Prescription must be verified before dispensing");

            // Проверяем consent пациента на доступ аптеки к его данным
            var orgId = config["PharmacyOrganizationId"] ?? "pharmacy-1";
            if (!await CheckConsent(prescription.PatientId, orgId, http, config))
                return Results.Json(
                    new { error = $"Patient {prescription.PatientId} has not granted consent to {orgId}" },
                    statusCode: 403);

            prescription.Status = PrescriptionStatus.Dispensed;
            prescription.DispensedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { id, dispensedAt = prescription.DispensedAt });
        });
    }

    private static async Task<bool> CheckConsent(
        Guid patientId, string organizationId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var patientApiUrl = config["PatientApiUrl"] ?? "http://patient-api:3001";
            var client = http.CreateClient();
            var resp = await client.GetAsync(
                $"{patientApiUrl}/api/consents/check?patientId={patientId}&organizationId={organizationId}");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static async Task<bool> VerifyWithZkpProver(
        ReceivedPrescription p, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var zkpUrl = config["ZkpProverUrl"] ?? "http://zkp-prover:3005";
            var client = http.CreateClient();

            var proof = JsonSerializer.Deserialize<JsonElement>(p.ProofJson ?? "{}");
            var publicSignals = JsonSerializer.Deserialize<JsonElement>(p.PublicSignalsJson ?? "[]");

            var res = await client.PostAsJsonAsync($"{zkpUrl}/verify", new
            {
                proof,
                publicSignals
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
