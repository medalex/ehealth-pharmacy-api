using EHealth.Pharmacy.Models;
using Microsoft.EntityFrameworkCore;

namespace EHealth.Pharmacy.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ReceivedPrescription> Prescriptions => Set<ReceivedPrescription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReceivedPrescription>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Status).HasConversion<string>();
        });
    }
}
