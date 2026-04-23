using crud_app_backend.Models;
using Microsoft.EntityFrameworkCore;

namespace crud_app_backend
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        // ── Keep only WhatsApp-related tables ────────────────────────────────
        public DbSet<WhatsAppSession>        WhatsAppSessions          { get; set; } = null!;
        public DbSet<WhatsAppSessionHistory> WhatsAppSessionHistories  { get; set; } = null!;
        public DbSet<WhatsAppMessage>        WhatsAppMessages          { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // ── WhatsAppMessages ──────────────────────────────────────────────
            modelBuilder.Entity<WhatsAppMessage>(entity =>
            {
                entity.ToTable("WhatsAppMessages", "dbo");
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id)
                      .HasDefaultValueSql("NEWSEQUENTIALID()");
                entity.Property(e => e.MessageId).HasMaxLength(255).IsRequired();
                entity.HasIndex(e => e.MessageId)
                      .IsUnique()
                      .HasDatabaseName("UX_WhatsAppMessages_MessageId");
                entity.Property(e => e.FromNumber).HasMaxLength(30).IsRequired();
                entity.HasIndex(e => new { e.FromNumber, e.ReceivedAt })
                      .HasDatabaseName("IX_WhatsAppMessages_FromNumber_ReceivedAt");
                entity.Property(e => e.SenderName).HasMaxLength(255);
                entity.Property(e => e.MessageType).HasMaxLength(20).IsRequired();
                entity.Property(e => e.TextBody).HasColumnType("nvarchar(max)");
                entity.Property(e => e.MediaId).HasMaxLength(255);
                entity.Property(e => e.FileUrl).HasMaxLength(2048);
                entity.Property(e => e.MimeType).HasMaxLength(100);
                entity.Property(e => e.Caption).HasMaxLength(1000);
                entity.Property(e => e.Status).HasMaxLength(30).IsRequired().HasDefaultValue("received");
                entity.Property(e => e.ErrorMessage).HasMaxLength(1000);
                entity.Property(e => e.ReceivedAt).IsRequired().HasDefaultValueSql("SYSUTCDATETIME()");
            });

            // ── WhatsAppSessions ──────────────────────────────────────────────
            modelBuilder.Entity<WhatsAppSession>(entity =>
            {
                entity.HasKey(e => e.Phone);
                entity.Property(e => e.Phone).HasMaxLength(30).IsRequired();
                entity.Property(e => e.CurrentStep).HasMaxLength(50);
                entity.Property(e => e.PreviousStep).HasMaxLength(50);
                entity.HasMany(e => e.History)
                      .WithOne()
                      .HasForeignKey(h => h.Phone)
                      .HasConstraintName("FK_SessionHistory_Phone")
                      .OnDelete(DeleteBehavior.Cascade);
            });

            // ── WhatsAppSessionHistories ──────────────────────────────────────
            modelBuilder.Entity<WhatsAppSessionHistory>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Phone).HasMaxLength(30).IsRequired();
                entity.Property(e => e.FromStep).HasMaxLength(50);
                entity.Property(e => e.ToStep).HasMaxLength(50);
                entity.HasIndex(e => new { e.Phone, e.CreatedAt })
                      .HasDatabaseName("IX_WhatsAppSessionHistory_Phone_CreatedAt");
            });
        }
    }
}
