using System.Collections.Immutable;
using System.Linq;

using Homespool.Data;
using Homespool.Host.Accounts;
using Homespool.Host.Cameras;
using Homespool.Host.Mail;
using Homespool.Host.Middleware;
using Homespool.Host.PrintFiles;
using Homespool.Host.PrusaConnect;

namespace Homespool.Host.Configuration;

/// <summary>
/// Every setting an administrator may change from the application, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>The grades were read off the consumers, one at a time, not assigned by category.</b> Two
/// settings in the same options class can differ: <c>Storage.MinimumSampleIntervalSeconds</c> is read
/// per sample and is live, while <c>Storage.WriteFlushIntervalSeconds</c> builds a
/// <c>PeriodicTimer</c> once when the writer's loop starts and cannot be. The rule for deciding a new
/// entry's grade is to find the read, not to look at the neighbours.
/// </para>
/// <para>
/// <b>A setting only half-obeyed is graded <see cref="SettingGrade.Restart"/>.</b>
/// <c>Storage.WriteBatchSize</c> sizes the writer's bounded channel at construction and is also read
/// per batch, so a change would move the batching threshold while the channel kept its old capacity.
/// Applying half of a number is harder to reason about than applying none of it, so the honest grade
/// is the lower one.
/// </para>
/// <para>
/// <b>What is deliberately absent is the larger half.</b> Listener ports must agree with what Docker
/// publishes; directory paths with what is mounted; the printer address and TLS flag are written into
/// a provisioning ini <i>and</i> a certificate minted once; the OIDC settings are consumed while the
/// authentication handler is being built. Those belong to whoever brings the stack up, and they stay
/// in <c>.env</c> - which is the choice this list encodes by omission.
/// </para>
/// </remarks>
public static class EditableSettings
{
    /// <summary>
    /// The allowlist, in the order a page would reasonably show it.
    /// </summary>
    public static readonly ImmutableArray<EditableSetting> All =
    [

        // Security - read per request at TwoFactorEnrolmentMiddleware:79.
        new(typeof(SecurityOptions), SecurityOptions.SectionName, nameof(SecurityOptions.RequireTwoFactor), SettingGrade.Live),

        // Attempt limits - read per check at AttemptLimiter:121,130.
        // Only the count. The two timing knobs are deliberately absent - see AttemptLimitOptions.
        new(typeof(AttemptLimitOptions), AttemptLimitOptions.SectionName, nameof(AttemptLimitOptions.MaxFailedAttempts), SettingGrade.Live),

        // Invitations - read when an invite is created, at InvitationService:73.
        new(typeof(InvitationOptions), InvitationOptions.SectionName, nameof(InvitationOptions.LifetimeHours), SettingGrade.Live),

        // Uploads - read per upload in PrintFileController, OctoPrintCompatController, Files/Index
        // and BoundedUploadAttribute.
        new(typeof(PrintFileStorageOptions), PrintFileStorageOptions.SectionName, nameof(PrintFileStorageOptions.MaxUploadBytes), SettingGrade.Live),

        // Printer protocol.
        new(typeof(PrusaConnectOptions), PrusaConnectOptions.SectionName, nameof(PrusaConnectOptions.MaxIncomingMessageBytes), SettingGrade.Live),
        new(typeof(PrusaConnectOptions), PrusaConnectOptions.SectionName, nameof(PrusaConnectOptions.RegistrationCodeLifetimeMinutes), SettingGrade.Live),

        // Read once per connection actor at PrinterConnectionActorFactory:41, so a printer already
        // connected keeps the timeout it was built with until it reconnects.
        new(typeof(PrusaConnectOptions),
            PrusaConnectOptions.SectionName,
            nameof(PrusaConnectOptions.CommandResponseTimeoutSeconds),
            SettingGrade.Deferred,
            AppliesWhenKey: "Settings_AppliesOnNextPrinterConnection"),

        // Telemetry ingest - read per sample at TelemetryWriter:733.
        new(typeof(StorageOptions), StorageOptions.SectionName, nameof(StorageOptions.MinimumSampleIntervalSeconds), SettingGrade.Live),

        // Retention - TelemetryRetentionService reads these inside each sweep, and SweepInterval is
        // one hour.
        new(typeof(StorageOptions),
            StorageOptions.SectionName,
            nameof(StorageOptions.TelemetryRetentionDays),
            SettingGrade.Deferred,
            AppliesWhenKey: "Settings_AppliesOnNextSweep"),
        new(typeof(StorageOptions),
            StorageOptions.SectionName,
            nameof(StorageOptions.EventRetentionDays),
            SettingGrade.Deferred,
            AppliesWhenKey: "Settings_AppliesOnNextSweep"),
        new(typeof(StorageOptions),
            StorageOptions.SectionName,
            nameof(StorageOptions.MaxEventsPerPrinter),
            SettingGrade.Deferred,
            AppliesWhenKey: "Settings_AppliesOnNextSweep"),

        // Sizes the writer's bounded channel at TelemetryWriter:234 and is read per batch at :547.
        new(typeof(StorageOptions), StorageOptions.SectionName, nameof(StorageOptions.WriteBatchSize), SettingGrade.Restart),

        // Builds a PeriodicTimer once when the writer's loop starts, at TelemetryWriter:462.
        new(typeof(StorageOptions), StorageOptions.SectionName, nameof(StorageOptions.WriteFlushIntervalSeconds), SettingGrade.Restart),

        // Cameras - all read at the point of use, in CameraFrameCache, CameraSnapshotFetcher and
        // CameraSourcePolicy.
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.RefreshFloorSeconds), SettingGrade.Live),
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.MaxAgeSeconds), SettingGrade.Live),
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.TimeoutSeconds), SettingGrade.Live),
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.MaxFrameBytes), SettingGrade.Live),
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.WebRtcStunServer), SettingGrade.Live),

        // Mail. Every one of these is Restart, and the reason is three startup decisions rather than
        // any one property: Program picks SmtpEmailSender or LoggingEmailSender from
        // SmtpOptions.IsConfigured, registers TelemetryAlertService only when mail is configured, and
        // has AccountConfirmationPolicy capture the confirm-at-creation rule once. That last one is
        // deliberate and documented where it is built - whether a new account is auto-confirmed must
        // not change under a running deployment.
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.Host), SettingGrade.Restart),
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.Port), SettingGrade.Restart),
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.UseImplicitTls), SettingGrade.Restart),
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.DisableTls), SettingGrade.Restart),
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.UserName), SettingGrade.Restart),
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.Password), SettingGrade.Restart, IsSecret: true),
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.FromAddress), SettingGrade.Restart),
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.FromName), SettingGrade.Restart),
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.TimeoutSeconds), SettingGrade.Restart),
        new(typeof(SmtpOptions), SmtpOptions.SectionName, nameof(SmtpOptions.ProbeOnStartup), SettingGrade.Restart),
    ];

    /// <summary>
    /// The configuration paths on the allowlist, for deciding whether a key may be written at all.
    /// </summary>
    public static readonly ImmutableHashSet<string> Paths =
        All.Select(setting => setting.Path).ToImmutableHashSet();

    /// <summary>
    /// The distinct sections the allowlist touches, in the order they first appear.
    /// </summary>
    public static readonly ImmutableArray<string> Sections =
        [.. All.Select(setting => setting.Section).Distinct()];

    /// <summary>
    /// Finds an allowlisted setting by its <see cref="EditableSetting.Path"/>.
    /// </summary>
    /// <param name="path">The path, in <c>Section:Key</c> form.</param>
    /// <returns>The setting, or <see langword="null"/> if that path is not editable.</returns>
    public static EditableSetting? Find(string path)
    {
        return All.FirstOrDefault(setting => setting.Path == path);
    }
}
