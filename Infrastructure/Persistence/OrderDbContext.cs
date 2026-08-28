using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options) {
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Order>(entity => {
            entity.HasIndex(e => e.OrderNumber).IsUnique();
            entity.HasIndex(e => e.CustomerId);
            entity.HasIndex(e => e.IdempotencyKey).IsUnique();

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(30);
        });

        modelBuilder.Entity<OrderItem>(entity => {
            entity.HasOne(d => d.Order)
                .WithMany(p => p.Items)
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
