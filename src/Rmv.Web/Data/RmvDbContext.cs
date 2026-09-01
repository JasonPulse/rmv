using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Rmv.Web.Data;

/// <summary>
/// Also the Data Protection key store. Without a shared key ring, ASP.NET Core
/// generates keys per process: sign-in cookies would break on every redeploy and
/// would not validate across replicas. Keeping the ring in Postgres means any
/// number of pods can decrypt each other's cookies.
/// </summary>
public class RmvDbContext(DbContextOptions<RmvDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Deployment> Deployments => Set<Deployment>();

    public DbSet<GamePresence> GamePresences => Set<GamePresence>();

    public DbSet<GameLink> GameLinks => Set<GameLink>();

    public DbSet<Member> Members => Set<Member>();

    public DbSet<Character> Characters => Set<Character>();

    public DbSet<CharacterPortrait> CharacterPortraits => Set<CharacterPortrait>();

    public DbSet<Screenshot> Screenshots => Set<Screenshot>();

    public DbSet<ScreenshotImage> ScreenshotImages => Set<ScreenshotImage>();

    public DbSet<SpellcraftTemplate> SpellcraftTemplates => Set<SpellcraftTemplate>();

    public DbSet<Signature> Signatures => Set<Signature>();

    public DbSet<SignatureImage> SignatureImages => Set<SignatureImage>();

    public DbSet<SignatureBackground> SignatureBackgrounds => Set<SignatureBackground>();

    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Deployment>(e =>
        {
            e.ToTable("deployments");
            e.Property(d => d.Version).HasMaxLength(64).IsRequired();
            e.Property(d => d.Host).HasMaxLength(128).IsRequired();
            // Newest-first is the only way this table is ever read.
            e.HasIndex(d => d.StartedAt).IsDescending();
        });

        b.Entity<GamePresence>(e =>
        {
            e.ToTable("game_presences");
            e.Property(g => g.Game).HasMaxLength(80).IsRequired();
            e.Property(g => g.Guilds).HasMaxLength(240).IsRequired();
            e.Property(g => g.Period).HasMaxLength(40);
            e.Property(g => g.HeraldAdapterKey).HasMaxLength(40);
            e.Property(g => g.HeraldBaseUrl).HasMaxLength(ExternalUrl.MaxLength);
            e.HasIndex(g => new { g.IsActive, g.SortOrder });

            // Seeded so the page has content the moment it deploys, and so the
            // admin screen has something to edit rather than an empty list.
            e.HasData(
                new GamePresence { Id = 1, Game = "Blackthorn DAoC", Guilds = "Dark Auspices", IsActive = true, SortOrder = 0 },
                new GamePresence { Id = 2, Game = "Uthgard DAoC", Guilds = "RMV, Legends, Dark Auspices", IsActive = false, SortOrder = 0 },
                new GamePresence { Id = 3, Game = "World of Warcraft", Guilds = "RMV, Omen, Etc.", IsActive = false, SortOrder = 1 },
                new GamePresence { Id = 4, Game = "Final Fantasy XI", Guilds = "RMV", IsActive = false, SortOrder = 2 });
        });

        b.Entity<GameLink>(e =>
        {
            e.ToTable("game_links");
            e.Property(l => l.Label).HasMaxLength(60).IsRequired();
            e.Property(l => l.Url).HasMaxLength(ExternalUrl.MaxLength).IsRequired();
            // Stored as text rather than an int, so the table reads on its own.
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.HasOne(l => l.Game)
                .WithMany(g => g.Links)
                .HasForeignKey(l => l.GamePresenceId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => new { l.GamePresenceId, l.SortOrder });

            // One seeded link, the herald Jason supplied, so the feature is
            // visible on deploy. The rest are his to add.
            e.HasData(new GameLink
            {
                Id = 1,
                GamePresenceId = 2,
                Kind = GameLinkKind.Herald,
                Label = "Uthgard Herald",
                Url = "https://herald.uthgard.net/herald.php?view=overview",
                SortOrder = 0,
            });
        });

        b.Entity<Member>(e =>
        {
            e.ToTable("members");
            e.Property(m => m.DiscordId).HasMaxLength(32).IsRequired();
            e.Property(m => m.DisplayName).HasMaxLength(80).IsRequired();
            e.Property(m => m.Alias).HasMaxLength(32);
            e.Property(m => m.AvatarHash).HasMaxLength(64);
            e.Property(m => m.ApprovedBy).HasMaxLength(80);
            // Stored as text so the table reads without a lookup.
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            e.HasIndex(m => m.Status);
            // The identity Discord guarantees is stable, so it is the natural key.
            e.HasIndex(m => m.DiscordId).IsUnique();
            e.HasIndex(m => m.IsAdmin);
        });

        b.Entity<Character>(e =>
        {
            e.ToTable("characters");
            e.Property(c => c.Name).HasMaxLength(CharacterLimits.MaxName).IsRequired();
            e.Property(c => c.Guild).HasMaxLength(80);
            e.Property(c => c.Realm).HasMaxLength(40);
            e.Property(c => c.Class).HasMaxLength(CharacterLimits.MaxClass);
            e.Property(c => c.Race).HasMaxLength(CharacterLimits.MaxRace);
            e.Property(c => c.RealmRank).HasMaxLength(40);
            e.Property(c => c.LastOnline).HasMaxLength(40);
            e.Property(c => c.HeraldUrl).HasMaxLength(ExternalUrl.MaxLength);
            // A digest of the picture's bytes, so a fixed width. See
            // CharacterService.VersionOf.
            e.Property(c => c.PortraitVersion).HasMaxLength(32);
            e.Property(c => c.LastError).HasMaxLength(300);
            // What the character's own herald publishes beyond these columns. jsonb
            // for the same reason a signature design is: Postgres refuses anything
            // that is not a document.
            e.Property(c => c.Stats).HasColumnType("jsonb").HasMaxLength(4_000);
            // Text, like MemberStatus, so the table reads without a lookup.
            e.Property(c => c.Source).HasConversion<string>().HasMaxLength(16).IsRequired();

            e.HasOne(c => c.Member).WithMany()
                .HasForeignKey(c => c.MemberId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(c => c.Game).WithMany(g => g.Characters)
                .HasForeignKey(c => c.GamePresenceId)
                .OnDelete(DeleteBehavior.Cascade);

            // One character belongs to one person. Enforced in the database, not
            // only in the handler, so a double submit cannot create two owners.
            e.HasIndex(c => new { c.GamePresenceId, c.Name }).IsUnique();
            e.HasIndex(c => c.MemberId);
            // The public roster reads characters by game, newest first.
            e.HasIndex(c => new { c.GamePresenceId, c.AddedAt });
        });

        b.Entity<CharacterPortrait>(e =>
        {
            e.ToTable("character_portraits");
            e.HasKey(p => p.CharacterId);
            e.Property(p => p.ContentType).HasMaxLength(40).IsRequired();
            e.Property(p => p.Version).HasMaxLength(32).IsRequired();

            // Its own table so the bytes are never loaded by a query that only
            // wanted a character's name. Nothing configures this as a required
            // navigation, so Include is always a deliberate act.
            e.HasOne(p => p.Character).WithOne(c => c.Portrait)
                .HasForeignKey<CharacterPortrait>(p => p.CharacterId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SpellcraftTemplate>(e =>
        {
            // The cap lives in the schema, not only in the handler. The unique
            // index means a member cannot hold two templates at the same ordinal,
            // and the check constraint means no ordinal exists outside 1 to the
            // cap. Together they leave no arrangement of concurrent forged posts
            // that ends in six rows.
            e.ToTable("spellcraft_templates", t => t.HasCheckConstraint(
                "ck_spellcraft_templates_ordinal",
                $"\"Ordinal\" >= 1 AND \"Ordinal\" <= {SpellcraftTemplate.MaxPerMember}"));

            e.Property(t => t.Name).HasMaxLength(SpellcraftTemplate.MaxNameLength).IsRequired();
            e.Property(t => t.Design)
                .HasMaxLength(Tools.Spellcraft.SpellcraftDesign.MaxEncodedLength)
                .IsRequired();

            e.HasOne(t => t.Member).WithMany()
                .HasForeignKey(t => t.MemberId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(t => new { t.MemberId, t.Ordinal }).IsUnique();
        });

        b.Entity<Screenshot>(e =>
        {
            e.ToTable("screenshots");
            e.Property(x => x.Caption).HasMaxLength(Gallery.GalleryLimits.MaxCaption).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(40).IsRequired();

            e.HasOne(x => x.Member).WithMany()
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);

            // A game being removed from the history must not take the screenshots
            // with it, so the link is dropped rather than the row.
            e.HasOne(x => x.Game).WithMany()
                .HasForeignKey(x => x.GamePresenceId)
                .OnDelete(DeleteBehavior.SetNull);

            // The gallery reads newest first, and a member's own page reads theirs.
            e.HasIndex(x => x.UploadedAt);
            e.HasIndex(x => new { x.MemberId, x.UploadedAt });
        });

        b.Entity<ScreenshotImage>(e =>
        {
            e.ToTable("screenshot_images");
            e.HasKey(x => x.ScreenshotId);

            e.HasOne(x => x.Screenshot).WithOne(s => s.Image)
                .HasForeignKey<ScreenshotImage>(x => x.ScreenshotId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Signature>(e =>
        {
            e.ToTable("signatures");

            // One per member, and one slug in the whole table. Both are enforced
            // here rather than only in the handler, because a forum post embeds the
            // slug for years and two designs answering to one address is not a thing
            // to discover later.
            e.HasIndex(x => x.MemberId).IsUnique();
            e.HasIndex(x => x.Slug).IsUnique();

            e.Property(x => x.Slug).HasMaxLength(Signature.SlugLength).IsRequired();
            e.Property(x => x.Design)
                .HasColumnType("jsonb")
                .HasMaxLength(Rmv.Web.Signature.SignatureLimits.MaxDesignLength)
                .IsRequired();

            e.HasOne(x => x.Member).WithMany()
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SignatureImage>(e =>
        {
            e.ToTable("signature_images");
            e.HasKey(x => x.SignatureId);

            e.Property(x => x.Version).HasMaxLength(32).IsRequired();
            e.Property(x => x.SourceVersion).HasMaxLength(32).IsRequired();

            // Its own table so a query about a signature does not carry the PNG.
            e.HasOne(x => x.Signature).WithOne(s => s.Image)
                .HasForeignKey<SignatureImage>(x => x.SignatureId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SignatureBackground>(e =>
        {
            e.ToTable("signature_backgrounds");

            e.Property(x => x.ContentType).HasMaxLength(40).IsRequired();
            e.Property(x => x.Name).HasMaxLength(60).IsRequired();

            e.HasOne(x => x.Member).WithMany()
                .HasForeignKey(x => x.MemberId)
                .OnDelete(DeleteBehavior.Cascade);

            // The picker reads a member's own, newest first.
            e.HasIndex(x => new { x.MemberId, x.UploadedAt });
        });

        b.Entity<RequestLog>(e =>
        {
            e.ToTable("request_logs");
            e.Property(r => r.Path)
                .HasMaxLength(Analytics.RequestLogMiddleware.MaxTextLength).IsRequired();
            e.Property(r => r.Method).HasMaxLength(10).IsRequired();
            e.Property(r => r.Referrer).HasMaxLength(Analytics.RequestLogMiddleware.MaxTextLength);
            e.Property(r => r.ReferrerHost).HasMaxLength(Analytics.RequestLogMiddleware.MaxHostLength);
            // The panel that answers "which site is sending these" groups on it.
            e.HasIndex(r => r.ReferrerHost);
            e.Property(r => r.UserAgent).HasMaxLength(Analytics.RequestLogMiddleware.MaxTextLength);
            e.Property(r => r.Country).HasMaxLength(2);
            // Every query is either recent-first or grouped by path over a window.
            e.HasIndex(r => r.At).IsDescending();
            e.HasIndex(r => new { r.Path, r.At });
            e.HasIndex(r => new { r.Status, r.At });
        });

        b.Entity<DataProtectionKey>().ToTable("data_protection_keys");
    }
}
