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
/// <c>DbContext</c>. <c>Program.cs</c> calls <c>AddEntityFrameworkStores&lt;HomespoolDbContext&gt;()</c>,
/// which requires the context to expose the Identity entity sets — with a plain
/// <c>DbContext</c> the Identity tables are never created and user store resolution fails at
/// startup. The key type is <see cref="long"/> to match <see cref="HSUser"/>.
/// </remarks>
public class HomespoolDbContext : IdentityDbContext<HSUser, IdentityRole<long>, long>, IDataProtectionKeyContext
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

    /// <summary>Index over the uploaded print files on disk. The filesystem is the truth; this exists
    /// so a queue entry can point at something a rename does not move.</summary>
    public DbSet<PrintFile> PrintFiles { get; set; }

    /// <summary>Per-printer print queues. One row per queued print; cancelling is deleting it.</summary>
    public DbSet<QueuedPrint> QueuedPrints { get; set; }

    /// <summary>What the loop believes each printer's drive holds of ours, and what the printer calls
    /// it. Keyed on (file, printer), so one file queued twice transfers once.</summary>
    public DbSet<PrintFileOnPrinter> PrintFilesOnPrinters { get; set; }

    /// <summary>Every print, running and finished - "print history" is the feature this backs. A row
    /// with no <c>EndedAt</c> is the print happening now.</summary>
    public DbSet<PrintJob> PrintJobs { get; set; }

    /// <summary>Cameras a still can be fetched from, optionally bound to a printer. Configuration
    /// only - no image is ever stored, here or anywhere.</summary>
    public DbSet<Camera> Cameras { get; set; }

    public HomespoolDbContext(DbContextOptions<HomespoolDbContext> options)
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
            // Bounded well below Identity's own 256 because it is rendered into every page header,
            // and because it is what a person types at a sign-in prompt. Uniqueness is not configured
            // here: Identity already indexes NormalizedUserName uniquely, which is the constraint that
            // matters, and duplicating it would be a second index over the same values.
            entity.Property(e => e.UserName)
                  .HasMaxLength(HSUser.UsernameMaxLength);

            entity.Property(e => e.NormalizedUserName)
                  .HasMaxLength(HSUser.UsernameMaxLength);

            // Homespool has no use for a phone number and no channel that would send to one: there is
            // no SMS two-factor provider registered, and the notification design rejected even email
            // as a channel (notes/filament-change-notification.md). The properties themselves are
            // IdentityUser's and cannot be removed short of not deriving from it; ignoring them keeps
            // the columns off the table, so the application never stores a number it cannot use.
            entity.Ignore(e => e.PhoneNumber);
            entity.Ignore(e => e.PhoneNumberConfirmed);
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

        builder.Entity<Camera>(entity =>
        {
            // The public identifier used in URLs, as on Printer, and looked up on every frame
            // request.
            entity.HasIndex(e => e.Uuid)
                  .IsUnique();

            // Listing cameras is scoped to their owning team, and a printer's page asks for the
            // cameras bound to it.
            entity.HasIndex(e => e.TeamId);
            entity.HasIndex(e => e.PrinterId);

            entity.Property(e => e.Name)
                  .HasMaxLength(Camera.NameMaxLength);

            entity.Property(e => e.Source)
                  .IsRequired()
                  .HasMaxLength(Camera.SourceMaxLength);

            // Same reasoning as Printer: deleting a team that still owns cameras should fail
            // loudly rather than quietly take them.
            entity.HasOne(e => e.Team)
                  .WithMany()
                  .HasForeignKey(e => e.TeamId)
                  .OnDelete(DeleteBehavior.Restrict);

            // Cascade, and deliberately not SetNull. Null on PrinterId already means "not bound to
            // a printer", so setting it null on delete would make a camera orphaned by a deletion
            // indistinguishable from one left unbound on purpose - which is exactly the trap
            // user-identity.md records against StoppedByUserId, where null already meant "stopped
            // at the panel". Restrict was the other candidate and is worse here: it would block
            // deleting a printer because a camera watches it.
            entity.HasOne(e => e.Printer)
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);
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

        builder.Entity<PrintFile>(entity =>
        {
            // The natural key: a user's files are unique by name, which is what makes the name the
            // identity and rename a first-class verb.
            entity.HasIndex(e => new { e.UserId, e.Name })
                  .IsUnique();

            // NOCASE so the uniqueness matches how UserFileStore resolves a name - it compares
            // case-insensitively on purpose, because macOS folds case, Linux does not, and the
            // printer's FAT32 would collide the two at /usb/ regardless.
            //
            // Note SQLite's NOCASE folds ASCII only, while the store uses full OrdinalIgnoreCase - so
            // this index is a slightly weaker backstop than the rule it backs, not an equal one. That
            // is acceptable precisely because it IS a backstop: the store is the gate, and the only
            // way to reach a pair it would have refused is meddling with the tree by hand.
            entity.Property(e => e.Name)
                  .UseCollation("NOCASE");

            // A deleted account takes its file index with it. The bytes on disk are a separate
            // question and nothing here deletes them - which is why the startup reconcile skips a
            // directory whose user no longer exists rather than re-inserting rows that cannot satisfy
            // this constraint.
            entity.HasOne<HSUser>()
                  .WithMany()
                  .HasForeignKey(e => e.UserId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<QueuedPrint>(entity =>
        {
            // The queue read, and the only one there is: this printer's entries, in order.
            entity.HasIndex(e => new { e.PrinterId, e.Position });

            // The public handle, looked up on every move/cancel - the Printer.Uuid arrangement.
            // Unique here and deliberately NOT on PrintJobs: an entry is one intention, but one
            // intention can leave several history rows (a full-drive hold writes a Failed row while
            // the entry stays queued), so uniqueness holds only on this side of the handoff.
            entity.HasIndex(e => e.TrackingId)
                  .IsUnique();

            // A deleted printer takes its queue with it. There is no meaning left in an entry whose
            // printer is gone, and unlike telemetry it is not history worth keeping.
            //
            // Navigation-less, like PrintJob and for the same reason - see QueuedPrint.PrinterId. The
            // relationship is unchanged; only the slot fix-up used to write into is gone.
            entity.HasOne<Printer>()
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Restrict, deliberately, and this is the one delete rule worth arguing for: cascading
            // here would let deleting a file silently cancel a print somebody else had queued.
            // print-queue.md reaches the same conclusion about the printer's own copy of a file -
            // "delete only when no queued print still wants it" - and the same instinct applies to ours.
            // PrintFileCatalog turns the resulting failure into a sentence rather than an exception.
            entity.HasOne(e => e.PrintFile)
                  .WithMany(e => e.QueuedPrints)
                  .HasForeignKey(e => e.PrintFileId)
                  .OnDelete(DeleteBehavior.Restrict);

            // QueuedByUserId deliberately has no foreign key. It records who asked - the same
            // "a record, not a pointer" treatment file-storage.md gives history rows - and an FK here
            // would add a second cascade path into a table PrintFile already cascades from, for no
            // reader that needs the join.
        });

        builder.Entity<PrintFileOnPrinter>(entity =>
        {
            // One row per (file, printer): the whole point is that a file queued twice on one printer
            // transfers once, which a second row would defeat.
            entity.HasIndex(e => new { e.PrinterId, e.PrintFileId })
                  .IsUnique();

            // Navigation-less, as QueuedPrint above.
            entity.HasOne<Printer>()
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Cascade, unlike QueuedPrint's Restrict on the same table. This row is knowledge about
            // somebody else's drive rather than an intention of ours, so there is nothing here to
            // protect a person from losing - and the bytes it describes are findable again through
            // the printer's own storage listing, which is what notes/print-queue.md relies on for
            // exactly this.
            entity.HasOne(e => e.PrintFile)
                  .WithMany()
                  .HasForeignKey(e => e.PrintFileId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PrintJob>(entity =>
        {
            // "What did this printer print, most recently first" is the only question history asks.
            entity.HasIndex(e => new { e.PrinterId, e.StartedAt });

            // Non-unique on purpose - see QueuedPrint's TrackingId index for why one handle may find
            // several rows here. "What became of my enqueue" is the lookup this serves.
            entity.HasIndex(e => e.TrackingId);

            // At most one active print per printer, enforced in the database because application code
            // cannot make it atomic - the same partial-index trick TeamMember uses for "exactly one
            // default team per user". Worth having for a reason this branch demonstrated: the
            // overlapping-pass defect was found by a unique index doing precisely this job, so two
            // prints started on one printer should be a failed insert rather than two rows nobody
            // notices.
            //
            // The filter is raw SQL, so the column name is the database's rather than the CLR
            // property path.
            entity.HasIndex(e => e.PrinterId)
                  .IsUnique()
                  .HasFilter("\"EndedAt\" IS NULL");

            // Navigation-less, like PrinterEvent and TelemetrySample: see PrintJob.PrinterId.
            entity.HasOne<Printer>()
                  .WithMany()
                  .HasForeignKey(e => e.PrinterId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Stored as text, like PrinterEvent.EventType: readable in a raw SQLite session and immune
            // to the enum being reordered. History is low-volume, so the bytes saved do not matter.
            entity.Property(e => e.State)
                  .HasConversion<string>();

            // No foreign key to PrintFile, deliberately: this records a name and a digest rather than
            // pointing at a row, so a renamed or deleted file leaves history intact. See PrintJob.

            // Nor to HSUser, for QueuedByUserId or StoppedByUserId - but for a different reason than
            // the one above, and the difference is worth keeping straight. Those two are genuinely
            // pointers rather than copied values, so "a record, not a pointer" does not carry here.
            // What carries is that an account is never hard deleted (Henrik, 2026-08-03: deactivate,
            // do not delete, or history starts losing its subjects), so neither column can dangle.
            //
            // And no delete behaviour would be right if one ever were: cascade erases the history,
            // restrict blocks the deletion, and set-null is the worst of the three on StoppedByUserId,
            // where null already means "stopped at the panel" and nulling it would rewrite what
            // happened rather than admit the account is gone.
        });
    }
}
