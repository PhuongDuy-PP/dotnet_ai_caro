using Microsoft.EntityFrameworkCore;

namespace CaroAIServer.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<OpeningPosition> OpeningPositions { get; set; }
        public DbSet<MoveRecommendation> MoveRecommendations { get; set; }
        public DbSet<GeneratedOpeningSequence> GeneratedOpeningSequences { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<OpeningPosition>()
                .Property(e => e.GamePhase)
                .HasConversion<string>();

            modelBuilder.Entity<MoveRecommendation>()
                .Property(e => e.ThreatLevel)
                .HasConversion<string>();
            
            modelBuilder.Entity<OpeningPosition>()
                .HasMany(op => op.MoveRecommendations)
                .WithOne(mr => mr.OpeningPosition)
                .HasForeignKey(mr => mr.PositionId);
        }
    }
} 