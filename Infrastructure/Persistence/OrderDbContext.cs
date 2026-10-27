using Microsoft.EntityFrameworkCore;
using Kinetix.OrderService.Domain.Entities;
using Kinetix.OrderService.Domain.Enums;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public class OrderDbContext(DbContextOptions<OrderDbContext> options) : DbContext(options) {
    public const string IdempotencyKeyIndex = "IX_orders_customer_principal_id_idempotency_key";
    public const string SagaOrderNumberIndex = "IX_checkout_sagas_order_number";
    public const string CustomerNewestFirstIndex = "ix_orders_customer_newest_first";

    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<CheckoutSaga> CheckoutSagas => Set<CheckoutSaga>();
    public DbSet<CheckoutSagaStep> CheckoutSagaSteps => Set<CheckoutSagaStep>();
    public DbSet<CompensationAttempt> CompensationAttempts => Set<CompensationAttempt>();
    public DbSet<ShippingSettlement> ShippingSettlements => Set<ShippingSettlement>();
    public DbSet<CourierDispatch> CourierDispatches => Set<CourierDispatch>();
    public DbSet<EscrowRelease> EscrowReleases => Set<EscrowRelease>();
    public DbSet<OrderReturn> OrderReturns => Set<OrderReturn>();
    public DbSet<OrderReturnLine> OrderReturnLines => Set<OrderReturnLine>();
    public DbSet<OrderLifecycleEvent> OrderLifecycleEvents => Set<OrderLifecycleEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ShippingSettlement>(entity => {
            entity.HasIndex(e => new { e.SettledAt, e.NextAttemptAt });
        });

        modelBuilder.Entity<EscrowRelease>(entity => {
            entity.HasIndex(e => e.NextAttemptAt)
                .HasDatabaseName("ix_escrow_releases_due")
                .HasFilter("released_at IS NULL");
        });

        modelBuilder.Entity<OrderReturn>(entity => {
            entity.HasIndex(e => e.OrderNumber).IsUnique();
            entity.HasIndex(e => new { e.MerchantPrincipalId, e.Status });

            entity.HasIndex(e => e.NextRefundAttemptAt)
                .HasDatabaseName("ix_order_returns_refund_due")
                .HasFilter("status = 'GOODS_RECEIVED'");

            entity.Property(e => e.Status)
                .HasConversion<string>()
                .HasMaxLength(30);

            entity.HasMany(e => e.Lines)
                .WithOne(line => line.Return)
                .HasForeignKey(line => line.ReturnNumber)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderReturnLine>(entity => {
            entity.HasIndex(e => e.ReturnNumber);
        });

        modelBuilder.Entity<OrderLifecycleEvent>(entity => {
            entity.Property(e => e.Sequence).UseIdentityAlwaysColumn();

            entity.Property(e => e.TransactionId)
                .HasColumnType("xid8")
                .HasDefaultValueSql("pg_current_xact_id()")
                .ValueGeneratedOnAdd();

            entity.HasIndex(e => new { e.TransactionId, e.Sequence })
                .HasDatabaseName("ix_order_lifecycle_events_position");

            entity.HasIndex(e => e.OrderNumber);

            entity.Property(e => e.Kind).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.ReturnStatus).HasConversion<string>().HasMaxLength(30);
            entity.Property(e => e.ActorKind).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<OrderEntity>(entity => {
            entity.HasIndex(e => e.OrderNumber).IsUnique();
            entity.HasIndex(e => new { e.CustomerPrincipalId, e.CreatedAt, e.Id })
                .HasDatabaseName(CustomerNewestFirstIndex);
            entity.HasIndex(e => new { e.CustomerPrincipalId, e.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName(IdempotencyKeyIndex);

            entity.HasIndex(e => e.CreatedAt)
                .HasDatabaseName("ix_orders_pending_payment")
                .HasFilter("status = 'PENDING_PAYMENT'");

            entity.HasIndex(e => new { e.UpdatedAt, e.OrderNumber })
                .HasDatabaseName("ix_orders_changed_since");

            entity.Property(e => e.ChangeTransactionId)
                .HasColumnType("xid8")
                .HasDefaultValueSql("pg_current_xact_id()")
                .ValueGeneratedOnAddOrUpdate();

            entity.HasIndex(e => new { e.ChangeTransactionId, e.OrderNumber })
                .HasDatabaseName("ix_orders_change_feed");

            entity.HasIndex(e => e.DeliveredAt)
                .HasDatabaseName("ix_orders_awaiting_completion")
                .HasFilter("status = 'DELIVERED'");

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
