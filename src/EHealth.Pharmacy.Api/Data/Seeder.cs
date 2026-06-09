using EHealth.Pharmacy.Models;

namespace EHealth.Pharmacy.Data;

public static class Seeder
{
    private static readonly Guid Pat1 = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static void Seed(AppDbContext db)
    {
        if (db.Prescriptions.Any()) return;

        db.Prescriptions.AddRange(
            new ReceivedPrescription
            {
                Id = Guid.Parse("00000000-0000-0000-0005-000000000001"),
                DrugId = 1,
                DrugName = "Amoxicillin",
                Dosage = "500mg 3x/day",
                PatientId = Pat1,
                StmtHash = "0xabc123seed",
                ProofJson = "{}",
                PublicSignalsJson = "[]",
                Outcome = true,
                Status = PrescriptionStatus.Received,
                ReceivedAt = DateTime.UtcNow.AddHours(-2)
            },
            new ReceivedPrescription
            {
                Id = Guid.Parse("00000000-0000-0000-0005-000000000002"),
                DrugId = 2,
                DrugName = "Metformin",
                Dosage = "850mg 2x/day",
                PatientId = Pat1,
                StmtHash = "0xdef456seed",
                ProofJson = "{}",
                PublicSignalsJson = "[]",
                Outcome = true,
                Status = PrescriptionStatus.Verified,
                ReceivedAt = DateTime.UtcNow.AddDays(-1),
                VerifiedAt = DateTime.UtcNow.AddHours(-23)
            }
        );

        db.SaveChanges();
    }
}
