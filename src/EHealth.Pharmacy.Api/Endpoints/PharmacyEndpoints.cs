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

            // On-chain verification only (no registry write yet): public inputs are pinned to
            // DKG governance and the proof is cryptographically verified by the on-chain Groth16
            // verifier. The immutable on-chain record happens at dispensing, after consent — so
            // the registry contains only dispensed prescriptions (paper R9).
            var outcome = await VerifyProofOnChain(prescription, record: false, http, config);
            switch (outcome)
            {
                case VerifyOutcome.Replay: // not expected without recording, handled defensively
                case VerifyOutcome.ProofInvalid:
                    prescription.Status = PrescriptionStatus.Rejected;
                    prescription.VerifiedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return Results.Ok(new { id, verified = false, status = "Rejected" });
                case VerifyOutcome.Unavailable:
                    prescription.Status = PrescriptionStatus.Rejected;
                    prescription.VerifiedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return Results.Json(new { id, verified = false, status = "Rejected", reason = "on-chain verifier unavailable" }, statusCode: 503);
                default:
                    prescription.Status = PrescriptionStatus.Verified;
                    prescription.VerifiedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                    return Results.Ok(new { id, verified = true, status = prescription.Status.ToString() });
            }
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

            // Consent first: the patient must have granted the pharmacy access.
            var orgId = config["PharmacyOrganizationId"] ?? "pharmacy-1";
            if (!await PatientApi.ConsentGranted(prescription.PatientId, orgId, http, config))
                return Results.Json(
                    new { error = $"Patient {prescription.PatientId} has not granted consent to {orgId}" },
                    statusCode: 403);

            // Only now write the decision to the immutable on-chain registry — so the registry
            // contains only dispensed prescriptions. 409 → already recorded (double dispense).
            var anchor = await VerifyProofOnChain(prescription, record: true, http, config);
            switch (anchor)
            {
                case VerifyOutcome.Replay:
                    return Results.Json(new { id, error = "replay: stmtHash already recorded on-chain" }, statusCode: 409);
                case VerifyOutcome.ProofInvalid:
                    return Results.BadRequest(new { id, error = "proof no longer valid at dispensing" });
                case VerifyOutcome.Unavailable:
                    return Results.Json(new { id, error = "on-chain registry unavailable" }, statusCode: 503);
            }

            prescription.Status = PrescriptionStatus.Dispensed;
            prescription.DispensedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            return Results.Ok(new { id, dispensedAt = prescription.DispensedAt });
        });
    }

    private enum VerifyOutcome { Verified, ProofInvalid, Replay, Unavailable }

    // Verifies the proof on-chain (Groth16 verifier contract) after pinning public inputs to
    // DKG governance + patient roots and checking freshness. When record=false the EVM node
    // only verifies (view); when record=true it also writes the decision to the immutable
    // registry — so the registry receives only dispensed prescriptions (consent checked first).
    // 409 → already recorded (record=true only).
    private static async Task<VerifyOutcome> VerifyProofOnChain(
        ReceivedPrescription p, bool record, IHttpClientFactory http, IConfiguration config)
    {
        try
        {
            var proof = JsonSerializer.Deserialize<JsonElement>(p.ProofJson ?? "{}");
            var publicSignals = JsonSerializer.Deserialize<JsonElement>(p.PublicSignalsJson ?? "[]");

            // Verifier-side pinning (application layer): public parameters must match the
            // DKG governance values and the patient's current record roots.
            if (!await PinPublicSignalsToGovernance(publicSignals, p.PatientId, http, config))
                return VerifyOutcome.ProofInvalid;

            // Off-circuit freshness check (the circuit no longer compares time in-circuit).
            if (!IsWithinValidity(publicSignals))
                return VerifyOutcome.ProofInvalid;

            var url = config["DecisionRegistryUrl"] ?? "http://decision-registry:3010";
            var endpoint = record ? "verify-and-record" : "verify";
            var client = http.CreateClient();
            var res = await client.PostAsJsonAsync($"{url}/{endpoint}", new { proof, publicSignals });
            if ((int)res.StatusCode == 409) return VerifyOutcome.Replay;
            if (!res.IsSuccessStatusCode) return VerifyOutcome.Unavailable;
            var result = await res.Content.ReadFromJsonAsync<VerifyResult>();
            return (result?.Verified ?? false) ? VerifyOutcome.Verified : VerifyOutcome.ProofInvalid;
        }
        catch { return VerifyOutcome.Unavailable; }
    }


    // Public-signal indices (must match the prover's PUB layout).
    private const int IdxIssuanceTime = 2;
    private const int IdxValidFor = 3;
    private const int IdxValidCredentialRoot = 4;
    private const int IdxPatientRecordRoot = 5;
    private const int IdxPolicyDrugId0 = 7;          // policyDrugIds → 7,8,9
    private const int IdxContraindicationRoot = 13;
    private const int IdxLabRecordRoot = 14;

    // Off-circuit freshness: the prescription must be within its validity window.
    // issuanceTime + validFor are public inputs (and bound into stmtHash), so they cannot
    // be forged; the dispensing-time check belongs here, where "now" is meaningful.
    private static bool IsWithinValidity(JsonElement publicSignals)
    {
        try
        {
            if (publicSignals.ValueKind != JsonValueKind.Array) return false;
            var ps = publicSignals.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
            if (ps.Length <= IdxValidFor) return false;
            var issued = long.Parse(ps[IdxIssuanceTime]);
            var validFor = long.Parse(ps[IdxValidFor]);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return now <= issued + validFor;
        }
        catch { return false; }
    }

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
            if (ps.Length < 21) return false; // circuit nPublic

            var mfssiaUrl = Mfssia.BaseUrl(config);
            var client = http.CreateClient();

            // Each committed root: where mfssia serves it, the property carrying it, and the
            // public signal that must equal it. The first four are single-valued.
            var pinned = new (string Url, string Property, int Index)[]
            {
                ($"{mfssiaUrl}/physician-registry/merkle-root", "root", IdxValidCredentialRoot),
                ($"{mfssiaUrl}/contraindication/root", "contraindicationRoot", IdxContraindicationRoot),
                ($"{mfssiaUrl}/patient-record/{patientId}/proof", "patientRecordRoot", IdxPatientRecordRoot),
                ($"{mfssiaUrl}/lab-record/{patientId}", "labRecordRoot", IdxLabRecordRoot),
            };

            foreach (var (url, property, index) in pinned)
            {
                var response = await client.GetFromJsonAsync<JsonElement>(url);
                if (!Mfssia.TryUnwrap(response, out var data) ||
                    !data.TryGetProperty(property, out var root)) return false;
                if (ps[index] != root.GetString()) return false;
            }

            // The drug formulary pins three consecutive signals rather than one.
            var drugsResp = await client.GetFromJsonAsync<JsonElement>($"{mfssiaUrl}/contraindication/drugs");
            if (!Mfssia.TryUnwrap(drugsResp, out var drugsData) ||
                !drugsData.TryGetProperty("drugIds", out var drugIds) ||
                drugIds.ValueKind != JsonValueKind.Array) return false;
            var expected = drugIds.EnumerateArray().Select(x => x.GetInt32().ToString()).ToArray();
            for (var i = 0; i < expected.Length && i < 3; i++)
                if (ps[IdxPolicyDrugId0 + i] != expected[i]) return false;

            return true;
        }
        catch { return false; }
    }

    private record ReceivePrescriptionRequest(
        int DrugId, string DrugName, string Dosage, Guid PatientId,
        string StmtHash, string ProofJson, string PublicSignalsJson, bool Outcome);

    private record VerifyResult(bool Verified);
}
