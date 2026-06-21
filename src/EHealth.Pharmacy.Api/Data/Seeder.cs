using EHealth.Pharmacy.Models;

namespace EHealth.Pharmacy.Data;

public static class Seeder
{
    private static readonly Guid Pat1 = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static void Seed(AppDbContext db)
    {
        // Intentionally empty — queue starts clean to demonstrate the full flow
    }
}
