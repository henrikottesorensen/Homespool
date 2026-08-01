using System;

using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

using Homespool.Model.Entities;

namespace Homespool.Data;

/// <summary>
/// The application database context.
/// </summary>
/// <remarks>
/// Derives from <see cref="IdentityDbContext{TUser,TRole,TKey}"/> rather than plain
/// <c>DbContext</c>. <c>Program.cs</c> calls <c>AddEntityFrameworkStores&lt;HSDbContext&gt;()</c>,
/// which requires the context to expose the Identity entity sets — with a plain
/// <c>DbContext</c> the Identity tables are never created and user store resolution fails at
/// startup. The key type is <see cref="long"/> to match <see cref="HSUser"/>.
/// </remarks>
public class HSDbContext : IdentityDbContext<HSUser, IdentityRole<long>, long>, IDataProtectionKeyContext
{
    public DbSet<Printer> Printers { get; set; }

    /// <summary>Enrolled printers' standing credentials, one row per enrolled printer. Read on the
    /// hot path of every authenticated request. Both enrolment channels converge here.</summary>
    public DbSet<PrusaConnectAuthenticationData> PrusaConnectAuthentication { get; set; }

    /// <summary>Pending code-exchange enrolments, from POST /p/register until the token is redeemed.
    /// Transient — deleted once the enrolled credential is materialised.</summary>
    public DbSet<PrusaConnectRegistration> PrusaConnectRegistrations { get; set; }

    /// <summary>Pending USB-key enrolments: a token pre-provisioned for a printer, bound to a
    /// fingerprint on first contact and then promoted to an enrolled credential.</summary>
    public DbSet<PrusaConnectProvisioning> PrusaConnectProvisionings { get; set; }

    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

    /// <summary>Last-known state, one row per printer. Upserted; never grows.</summary>
    public DbSet<PrinterLiveState> PrinterLiveStates { get; set; }

    /// <summary>Append-only dense telemetry history. Subject to retention.</summary>
    public DbSet<TelemetrySample> TelemetrySamples { get; set; }

    /// <summary>Discrete printer events. Retained indefinitely.</summary>
    public DbSet<PrinterEvent> PrinterEvents { get; set; }

    /// <summary>Last-known per-slot state. Empty for single-tool printers.</summary>
    public DbSet<PrinterLiveSlotState> PrinterLiveSlotStates { get; set; }

    /// <summary>Per-slot telemetry history. Swept by cascade when its parent sample is deleted.</summary>
    public DbSet<TelemetrySlotSample> TelemetrySlotSamples { get; set; }

    /// <summary>Ownership groups. Printers belong to a team; every user has a default one.</summary>
    public DbSet<Team> Teams { get; set; }

    /// <summary>Team membership with per-team permissions. One row per user per team.</summary>
    public DbSet<TeamMember> TeamMembers { get; set; }

    /// <summary>Outstanding and spent invitations. Single-use, email-bound, expiring.</summary>
    public DbSet<Invitation> Invitations { get; set; }

    /// <summary>Personal access tokens for the app API. Read on the hot path of every bearer-
    /// authenticated request; revoking one is deleting its row.</summary>
    public DbSet<ApiToken> ApiTokens { get; set; }

    public HSDbContext(DbContextOptions<HSDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Stores every <see cref="DateTimeOffset"/> as epoch milliseconds in an INTEGER column.
    /// </summary>
    /// <remarks>
    /// Applied as a convention rather than property by property, so a timestamp added later cannot
    /// silently fall back to the untranslatable TEXT mapping. See
    /// <see cref="DateTimeOffsetToUnixMillisecondsConverter"/> for why the default mapping — and EF's
    /// own <c>DateTimeOffsetToBinaryConverter</c> — are both unusable here. This also covers
    /// Identity's own <c>LockoutEnd</c>.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<DateTimeOffset>()
                            .HaveConversion<DateTimeOffsetToUnixMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<HSUser>(entity =>
        {
            // Bounded because it is rendered into every page header. Not unique and not indexed:
            // it identifies nobody - UserName still does that - so two people may share one.
            entity.Property(e => e.DisplayName)
                  .HasMaxLength(HSUser.DisplayNameMaxLength);
        });

        builder.Entity<Printer>(entity =>
        {
            // The public identifier used in URLs, so it is looked up on every such request and
            // must be unique. Deliberately not the primary key — see Printer.Id.
            entity.HasIndex(e => e.Uuid)
                  .IsUnique();

            // Listing printers is always scoped to their owning team.
            entity.HasIndex(e => e.TeamId);

            // Printers belong to a team. Restrict, not cascade: deleting a team that still owns
            // printers should fail loudly rather than silently take the printers (and their
            // telemetry) with it. Team lifecycle — reassigning or blocking deletion of a team with
            // printers — is a phase-1.5 open question, not resolved by a cascade here.
            entity.HasOne(e => e.Team)
                  .WithMany()
                  .HasForeignKey(e => e.TeamId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PrusaConnectAuthenticationData>(entity =>
        {
            // A printer is identified on the wire by its fingerprint, on every request and on
            // the WebSocket upgrade (AGENT-NOTES §9). That lookup is the hot path for every
            // connection, and two rows sharing a fingerprint would make identity ambiguous.
            //
            // Keyed on the truncated 16-character form the headers carry, not the 50-character form
            // /p/register's body carries, so both enrolment channels agree on what "the same printer"
            // means - see PrinterFingerprint.
            entity.HasIndex(e => e.FingerPrintKey)
                  .IsUnique();

            // The enrolled credential belongs to the printer: deleting the printer takes it with
            // it. PrinterId is required now (enrolled means a printer exists), so this is a required
            // relationship — cascade is the natural, and EF's default, behaviour for one.
            entity.HasOne(e => e.Printer)
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PrusaConnectRegistration>(entity =>
        {
            // GetPrinterCode looks a pending registration up by fingerprint (to renew rather than
            // duplicate a code); at most one may be in flight per fingerprint. Distinct from the
            // enrolled table's fingerprint index — a re-registering printer can briefly hold a
            // pending row here and a stale enrolled row there, in different tables.
            entity.HasIndex(e => e.FingerPrint)
                  .IsUnique();

            // The poll (GET /p/register) looks the row up by code. Deliberately NOT unique: a
            // collision should surface as SingleOrDefaultAsync throwing rather than being impossible.
            // At 10 Crockford base32 characters that is 2^50, against at most a handful of codes
            // pending at once, so it will not happen - but "will not" is the reason to let it throw
            // rather than the reason to make it unrepresentable.
            entity.HasIndex(e => e.TemporaryCode);

            // Nullable FK: the printer does not exist until a user claims the code. Cascade so a
            // deleted printer takes any pending registration referencing it along.
            entity.HasOne(e => e.Printer)
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PrusaConnectProvisioning>(entity =>
        {
            // One outstanding provisioning token per printer; regenerate overwrites it in place. A
            // row exists only while unbound — first contact promotes it into the enrolled table and
            // deletes it — so no fingerprint or status column is needed here.
            entity.HasIndex(e => e.PrinterId)
                  .IsUnique();

            entity.HasOne(e => e.Printer)
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PrinterLiveState>(entity =>
        {
            // 1:1 with Printer, sharing the primary key. Navigation-less on both sides - the FK and
            // cascade are all the relationship needs, and TelemetryWriter's long-lived instances of
            // this type must hold nothing a DbContext could have written into them (see the entity's
            // own remarks).
            entity.HasOne<Printer>()
                  .WithOne()
                  .HasForeignKey<PrinterLiveState>(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TelemetrySample>(entity =>
        {
            // Every read is "this printer, this time range" — charts, stats, and the retention
            // sweep alike. Leading PrinterId so the range scan stays contiguous.
            entity.HasIndex(e => new { e.PrinterId, e.Timestamp });

            // Navigation-less: see the entity's PrinterId comment.
            entity.HasOne<Printer>()
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PrinterLiveSlotState>(entity =>
        {
            // Natural composite key: one row per printer per slot, which makes the merge an upsert.
            entity.HasKey(e => new { e.PrinterId, e.SlotNumber });

            entity.HasOne(e => e.PrinterLiveState)
                  .WithMany(e => e.Slots)
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<TelemetrySlotSample>(entity =>
        {
            // A sample cannot report the same slot twice; enforcing it also serves the
            // "slots for this sample" lookup.
            entity.HasIndex(e => new { e.TelemetrySampleId, e.SlotNumber })
                  .IsUnique();

            // Cascade matters for retention: the sweep issues a bulk delete against
            // TelemetrySamples, and the database removes the slot rows. That requires
            // PRAGMA foreign_keys = ON, which SqlitePragmaInterceptor sets per connection.
            entity.HasOne(e => e.TelemetrySample)
                  .WithMany(e => e.Slots)
                  .HasForeignKey(e => e.TelemetrySampleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PrinterEvent>(entity =>
        {
            entity.HasIndex(e => new { e.PrinterId, e.Timestamp });

            // "What happened during job N" is the question the job view asks.
            entity.HasIndex(e => new { e.PrinterId, e.JobId });

            // Navigation-less: see the entity's PrinterId comment.
            entity.HasOne<Printer>()
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Stored as text: readable in a raw SQLite session, and immune to reordering of
            // the enum. The volume does not justify the two bytes saved.
            entity.Property(e => e.EventType)
                  .HasConversion<string>();
        });

        builder.Entity<TeamMember>(entity =>
        {
            // One row per user per team.
            entity.HasKey(e => new { e.TeamId, e.UserId });

            entity.HasOne(e => e.Team)
                  .WithMany(e => e.Members)
                  .HasForeignKey(e => e.TeamId)
                  .OnDelete(DeleteBehavior.Cascade);

            // "A user must have exactly one default team" enforced in the database rather than in
            // application code, which cannot make it atomic. SQLite honours partial indexes, so the
            // uniqueness applies only to the rows where IsDefault is set: a user has any number of
            // non-default memberships but at most one default. EF emits the WHERE clause from
            // HasFilter; the column name is the raw SQL identifier, so it is "IsDefault", not the
            // CLR property path.
            entity.HasIndex(e => e.UserId)
                  .IsUnique()
                  .HasFilter("\"IsDefault\"");
        });

        builder.Entity<Invitation>(entity =>
        {
            // Accept looks the invite up by the hash of the presented token, so that lookup must be
            // indexed and cannot collide. Two invites sharing a hash would make acceptance ambiguous.
            entity.HasIndex(e => e.HashedToken)
                  .IsUnique();

            // A nullable team target: null invites mint a new account with its own default team,
            // non-null ones join an existing team. Restrict so an invite cannot outlive its target
            // team silently — a dangling team id would send accept down the wrong branch.
            entity.HasOne(e => e.Team)
                  .WithMany()
                  .HasForeignKey(e => e.TeamId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ApiToken>(entity =>
        {
            // The whole scheme rests on this index: authentication hashes the presented secret and
            // looks the row up by it, so finding a row IS verifying the credential. Unique because two
            // rows sharing a hash would make "who is this" ambiguous — and, since the hash is
            // unsalted SHA-384 of 32 random bytes, a collision means the same secret was issued twice,
            // which the index turns into a failed insert rather than a silent ambiguity.
            entity.HasIndex(e => e.TokenHash)
                  .IsUnique();

            // The management page lists a person's own tokens, and nothing ever lists them all.
            entity.HasIndex(e => e.UserId);

            entity.Property(e => e.Name)
                  .HasMaxLength(ApiToken.NameMaxLength);

            // Cascade: a deleted account takes its credentials with it. Leaving them would leave live
            // bearer tokens pointing at a user id that no longer resolves.
            entity.HasOne<HSUser>()
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
