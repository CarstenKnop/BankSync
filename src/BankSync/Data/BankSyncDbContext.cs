using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BankSync.Data;

internal sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.ToUnixTimeMilliseconds(),
    v => DateTimeOffset.FromUnixTimeMilliseconds(v));

internal sealed class BankSyncDbContext(DbContextOptions<BankSyncDbContext> options) : DbContext(options)
{
    public DbSet<ConsentEntity> Consents => Set<ConsentEntity>();
    public DbSet<PendingAuthorizationEntity> PendingAuthorizations => Set<PendingAuthorizationEntity>();
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<ConsentAccountEntity> ConsentAccounts => Set<ConsentAccountEntity>();
    public DbSet<TransactionEntity> Transactions => Set<TransactionEntity>();
    public DbSet<BalanceSnapshotEntity> Balances => Set<BalanceSnapshotEntity>();
    public DbSet<SyncRunEntity> SyncRuns => Set<SyncRunEntity>();
    public DbSet<ApiCallEntity> ApiCalls => Set<ApiCallEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // SQLite cannot compare DateTimeOffset columns; Unix milliseconds compare and sort as integers.
        b.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ConsentEntity>(e =>
        {
            e.ToTable("Consents");
            e.HasIndex(x => x.Status);
        });

        b.Entity<PendingAuthorizationEntity>(e =>
        {
            e.ToTable("PendingAuthorizations");
            e.HasIndex(x => x.State).IsUnique();
        });

        b.Entity<AccountEntity>(e =>
        {
            e.ToTable("Accounts");
            e.HasIndex(x => x.IdentificationHash).IsUnique();
        });

        b.Entity<ConsentAccountEntity>(e =>
        {
            e.ToTable("ConsentAccounts");
            e.HasKey(x => new { x.ConsentId, x.AccountId });
            e.HasOne(x => x.Consent).WithMany(c => c.Accounts).HasForeignKey(x => x.ConsentId);
            e.HasOne(x => x.Account).WithMany(a => a.Consents).HasForeignKey(x => x.AccountId);
        });

        b.Entity<TransactionEntity>(e =>
        {
            e.ToTable("Transactions");
            e.HasIndex(x => new { x.AccountId, x.DedupHash }).IsUnique();
            e.HasIndex(x => new { x.AccountId, x.BookingDate });
            e.HasIndex(x => x.Status);
            e.Property(x => x.Amount).HasConversion<double>();        // SQLite has no decimal; double is fine for display and sums here
            e.Property(x => x.BalanceAfter).HasConversion<double?>();
        });

        b.Entity<BalanceSnapshotEntity>(e =>
        {
            e.ToTable("BalanceSnapshots");
            e.HasIndex(x => new { x.AccountId, x.BalanceType, x.FetchedUtc });
            e.Property(x => x.Amount).HasConversion<double>();
        });

        b.Entity<SyncRunEntity>(e =>
        {
            e.ToTable("SyncRuns");
            e.HasIndex(x => x.StartedUtc);
        });

        b.Entity<ApiCallEntity>(e =>
        {
            e.ToTable("ApiCalls");
            e.HasIndex(x => new { x.AccountId, x.LocalDay, x.Kind, x.Unattended });
        });

        b.Entity<SettingEntity>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(x => x.Key);
        });
    }
}
