using AgrocultivoWebSync.Models;
using Microsoft.EntityFrameworkCore;

namespace AgrocultivoWebSync.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<QuickBooksWebConnection> QuickBooksWebConnections =>
        Set<QuickBooksWebConnection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuickBooksWebConnection>(entity =>
        {
            entity.HasKey(x => x.Id);

            entity.Property(x => x.RealmId)
                .IsRequired();

            entity.Property(x => x.AccessToken)
                .IsRequired();

            entity.Property(x => x.RefreshToken)
                .IsRequired();

            entity.HasIndex(x => x.RealmId)
                .IsUnique();
        });
    }
}