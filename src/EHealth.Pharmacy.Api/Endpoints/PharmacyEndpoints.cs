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

            // Replay guard: a proof (stmtHash) may be accepted only once. If another
            // prescription with the same stmtHash was already verified/dispensed, this is
            // a replay of the same proof — reject. (Prototype-level spent registry; an
            // on-chain immutable registry is the production form — see paper R9.)
            var replayed = await db.Prescriptions.AnyAsync(x =>
                x.Id != id && x.StmtHash == prescription.StmtHash &&
                (x.Status == PrescriptionStatus.Verified || x.Status == PrescriptionStatus.Dispensed));
            if (replayed)
            {
                prescription.Status = PrescriptionStatus.Rejected;
                prescription.VerifiedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return Results.Ok(new { id, verified = false, status = "Rejected", reason = "replay: stmtHash already used" });
            }

            var isValid = await VerifyWithZkpProver(prescription, http, config);
            if (!isValid)
            {
                prescription.Status = PrescriptionStatus.Rejected;
                prescription.VerifiedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return Results.Ok(new { id, verified = false, status = "Rejected" });
            }

            // Anchor the accepted decision in the on-chain decision registry (immutable
            // audit trail + authoritative on-chain replay protection — paper R9).
            var anchor = await RecordDecisionOnChain(prescription.StmtHash, prescription.Outcome, http, config);
            if (anchor == AnchorResult.Replay)
            {
                prescription.Status = PrescriptionStatus.Rejected;
                prescription.VerifiedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return Results.Ok(new { id, verified = false, status = "Rejected", reason = "replay: stmtHash already recorded on-chain" });
            }
            if (anchor == AnchorResult.Unavailable)
            {
                prescription.Status = PrescriptionStatus.Rejected;
                prescription.VerifiedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return Results.Json(new { id, verified = false, status = "Rejected", reason = "decision registry unavailable" }, statusCode: 503);
            }

            prescription.Status = PrescriptionStatus.Verified;
            prescription.VerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { id, verified = true, status = prescription.Status.ToString() });
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

            // Check patient consent for pharmacy data access
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

    private enum AnchorResult { Recorded, Replay, Unavailable }

    // Records the accepted decision (stmtHash, outcome) in the on-chain decision registry.
    // 409 → the proof was already recorded (replay); non-success/unreachable → unavailable.
    private static async Task<AnchorResult> RecordDecisionOnChain(
        string? stmtHash, bool outcome, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var url = config["DecisionRegistryUrl"] ?? "http://decision-registry:3010";
            var client = http.CreateClient();
            var resp = await client.PostAsJsonAsync($"{url}/record", new { stmtHash, outcome });
            if ((int)resp.StatusCode == 409) return AnchorResult.Replay;
            return resp.IsSuccessStatusCode ? AnchorResult.Recorded : AnchorResult.Unavailable;
        }
        catch { return AnchorResult.Unavailable; }
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

            // Verifier-side pinning: the public parameters carried in the proof must match
            // the DKG governance + patient roots — a valid proof over fabricated public
            // inputs (forged formulary, registry root, or patient/lab record root) is rejected.
            if (!await PinPublicSignalsToGovernance(publicSignals, p.PatientId, http, config))
                return false;

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

    // Public-signal indices (must match the prover's PUB layout).
    private const int IdxValidCredentialRoot = 3;
    private const int IdxPatientRecordRoot = 4;
    private const int IdxPolicyDrugId0 = 6;          // policyDrugIds → 6,7,8
    private const int IdxContraindicationRoot = 12;
    private const int IdxLabRecordRoot = 13;

    // Checks that the proof's public parameters equal the DKG-committed governance values
    // (credential registry root, drug formulary, contraindication-closure root) and the
    // patient's current DKG record roots (allergy + lab), pinning every public input.
    private static async Task<bool> PinPublicSignalsToGovernance(
        JsonElement publicSignals, Guid patientId, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            if (publicSignals.ValueKind != JsonValueKind.Array) return false;
            var ps = publicSignals.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (ps.Length < 20) return false; // circuit nPublic

            var mfssiaUrl = config["MfssiaUrl"] ?? "http://mfssia-ehealth:4000/api";
            var client = http.CreateClient();

            // Physician registry root.
            var credResp = await client.GetFromJsonAsync<JsonElement>($"{mfssiaUrl}/physician-registry/merkle-root");
            if (!credResp.TryGetProperty("data", out var credData) ||
                !credData.TryGetProperty("root", out var credRoot)) return false;
            if (ps[IdxValidCredentialRoot] != credRoot.GetString()) return false;

            // Contraindication-closure root.
            var contraResp = await client.GetFromJsonAsync<JsonElement>($"{mfssiaUrl}/contraindication/root");
            if (!contraResp.TryGetProperty("data", out var contraData) ||
                !contraData.TryGetProperty("contraindicationRoot", out var contraRoot)) return false;
            if (ps[IdxContraindicationRoot] != contraRoot.GetString()) return false;

            // Drug formulary (policyDrugIds).
            var drugsResp = await client.GetFromJsonAsync<JsonElement>($"{mfssiaUrl}/contraindication/drugs");
            if (!drugsResp.TryGetProperty("data", out var drugsData) ||
                !drugsData.TryGetProperty("drugIds", out var drugIds) ||
                drugIds.ValueKind != JsonValueKind.Array) return false;
            var expected = drugIds.EnumerateArray().Select(x => x.GetInt32().ToString()).ToArray();
            for (var i = 0; i < expected.Length && i < 3; i++)
                if (ps[IdxPolicyDrugId0 + i] != expected[i]) return false;

            // Patient allergy-record root (per-patient, from DKG).
            var recResp = await client.GetFromJsonAsync<JsonElement>($"{mfssiaUrl}/patient-record/{patientId}/proof");
            if (!recResp.TryGetProperty("data", out var recData) ||
                !recData.TryGetProperty("patientRecordRoot", out var recRoot)) return false;
            if (ps[IdxPatientRecordRoot] != recRoot.GetString()) return false;

            // Patient lab-record root (per-patient, from DKG).
            var labResp = await client.GetFromJsonAsync<JsonElement>($"{mfssiaUrl}/lab-record/{patientId}");
            if (!labResp.TryGetProperty("data", out var labData) ||
                !labData.TryGetProperty("labRecordRoot", out var labRoot)) return false;
            if (ps[IdxLabRecordRoot] != labRoot.GetString()) return false;

            return true;
        }
        catch { return false; }
    }

    private record ReceivePrescriptionRequest(
        int DrugId, string DrugName, string Dosage, Guid PatientId,
        string StmtHash, string ProofJson, string PublicSignalsJson, bool Outcome);

    private record VerifyResult(bool Valid);
}
