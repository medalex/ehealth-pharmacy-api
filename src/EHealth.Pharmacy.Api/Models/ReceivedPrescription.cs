namespace EHealth.Pharmacy.Models;

public enum PrescriptionStatus { Received, Verified, Dispensed, Rejected }

public class ReceivedPrescription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // Drug info forwarded from hospital
    public int DrugId { get; set; }
    public string DrugName { get; set; } = default!;
    public string Dosage { get; set; } = default!;

    // Anonymous patient reference — no PII, used for audit trail only
    public Guid PatientId { get; set; }

    // ZKP proof bundle from hospital
    public string StmtHash { get; set; } = default!;
    public string ProofJson { get; set; } = default!;
    public string PublicSignalsJson { get; set; } = default!;
    public bool Outcome { get; set; }

    public PrescriptionStatus Status { get; set; } = PrescriptionStatus.Received;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAt { get; set; }
    public DateTime? DispensedAt { get; set; }
}
