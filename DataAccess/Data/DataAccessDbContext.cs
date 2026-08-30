using Microsoft.EntityFrameworkCore;
using DataAccess.Models;

namespace DataAccess.Data;

/// <summary>
/// Central PostgreSQL DbContext for DataAccess service (Code-First).
/// </summary>
public class DataAccessDbContext : DbContext
{
    public DataAccessDbContext(DbContextOptions<DataAccessDbContext> options) : base(options)
    {
    }

    public DbSet<Passenger> Passengers => Set<Passenger>();
    public DbSet<CheckinRecord> CheckinRecords => Set<CheckinRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Passenger>(entity =>
        {
            entity.ToTable("Passengers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(255);
            entity.Property(e => e.PassportNumber).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).IsRequired();
        });

        modelBuilder.Entity<CheckinRecord>(entity =>
        {
            entity.ToTable("CheckinRecords");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PassengerId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.FlightNumber).IsRequired().HasMaxLength(20);
            entity.Property(e => e.SeatNumber).IsRequired().HasMaxLength(10);
            entity.Property(e => e.Status).IsRequired().HasMaxLength(50);
            entity.Property(e => e.CreatedAt).IsRequired();
            entity.HasIndex(e => e.PassengerId);
        });
    }
}
