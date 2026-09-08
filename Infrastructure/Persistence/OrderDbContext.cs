using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options) {
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<CheckoutSaga> CheckoutSagas => Set<CheckoutSaga>();
    public DbSet<CheckoutSagaStep> CheckoutSagaSteps => Set<CheckoutSagaStep>();
    public DbSet<CompensationAttempt> CompensationAttempts => Set<CompensationAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OrderEntity>(entity => {
            entity.HasIndex(e => e.OrderNumber).IsUnique();
            entity.HasIndex(e => e.CustomerPrincipalId);
            entity.HasIndex(e => new { e.CustomerPrincipalId, e.IdempotencyKey }).IsUnique();

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(30);

            entity.Property(e => e.ShippingQuoteBasis)
                .HasConversion<string>()
                .HasMaxLength(20);
        });

        modelBuilder.Entity<CheckoutSaga>(entity => {
            entity.HasIndex(e => e.OrderNumber).IsUnique();

            entity.HasIndex(e => new { e.NextAttemptAt, e.UpdatedAt })
                .HasDatabaseName("ix_checkout_sagas_due")
                .HasFilter("state IN ('Running', 'Compensating', 'Stuck')");

            entity.HasIndex(e => e.NeedsAttentionAt)
                .HasDatabaseName("ix_checkout_sagas_attention")
                .HasFilter("needs_attention_at IS NOT NULL");

            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(20);

            entity.HasMany(e => e.Steps)
                .WithOne(s => s.Saga)
                .HasForeignKey(s => s.SagaId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CheckoutSagaStep>(entity => {
            entity.Property(e => e.Name).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.State).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<CompensationAttempt>(entity => {
            entity.HasIndex(e => new { e.SagaId, e.AttemptNo }).IsUnique();

            entity.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(20);

            entity.HasOne(e => e.Saga)
                .WithMany()
                .HasForeignKey(e => e.SagaId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderItem>(entity => {
            entity.HasOne(d => d.Order)
                .WithMany(p => p.Items)
                .HasForeignKey(d => d.OrderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
