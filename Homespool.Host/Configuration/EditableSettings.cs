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
        // Asked about before it is turned on: what it does is invisible from the outcome, since the
        // integrations it breaks belong to other people and fail as a 401 that explains nothing.
        new(typeof(SecurityOptions),
            SecurityOptions.SectionName,
            nameof(SecurityOptions.RequireTwoFactor),
            SettingGrade.Live,
            ConfirmOnEnableKey: "Settings_Confirm_Security_RequireTwoFactor"),

        // Read once, when the passkey scheme's options are first built - see
        // AuthenticationBuilderExtensions.AddPasskeyAuthentication. Beside RequireTwoFactor because
        // that is what it interacts with: a passkey is a sign-in of its own.
        new(typeof(SecurityOptions),
            SecurityOptions.SectionName,
            nameof(SecurityOptions.PasskeyServerDomain),
            SettingGrade.Restart),

        // Attempt limits - read per check at AttemptLimiter:121,130.
        // Only the count. The two timing knobs are deliberately absent - see AttemptLimitOptions.
        // Shown under the account heading rather than its own: it is bound from a different class,
        // which is not a reason to give a reader a second heading for the same subject.
        new(typeof(AttemptLimitOptions),
            AttemptLimitOptions.SectionName,
            nameof(AttemptLimitOptions.MaxFailedAttempts),
            SettingGrade.Live,
            DisplayGroup: SecurityOptions.SectionName),

        // Invitations - read when an invite is created, at InvitationService:73.
        new(typeof(InvitationOptions), InvitationOptions.SectionName, nameof(InvitationOptions.LifetimeHours), SettingGrade.Live),

        // Printer protocol.
        new(typeof(PrusaConnectOptions), PrusaConnectOptions.SectionName, nameof(PrusaConnectOptions.RegistrationCodeLifetimeMinutes), SettingGrade.Live),

        // Read once per connection actor at PrinterConnectionActorFactory:41, so a printer already
        // connected keeps the timeout it was built with until it reconnects.
        new(typeof(PrusaConnectOptions),
            PrusaConnectOptions.SectionName,
            nameof(PrusaConnectOptions.CommandResponseTimeoutSeconds),
            SettingGrade.Deferred,
            AppliesWhenKey: "Settings_AppliesOnNextPrinterConnection"),

        // What this deployment stores, of both kinds. Uploads sit here rather than under a heading of
        // their own because "how much disk does this take" is one question, and a reader should not
        // have to know that files and telemetry are bound from different classes to find it. Read per
        // upload in PrintFileController, OctoPrintCompatController, Files/Index and
        // BoundedUploadAttribute.
        new(typeof(PrintFileStorageOptions),
            PrintFileStorageOptions.SectionName,
            nameof(PrintFileStorageOptions.MaxUploadBytes),
            SettingGrade.Live,
            DisplayGroup: StorageOptions.SectionName,
            DisplaySubgroup: "PrintFiles"),

        // Telemetry ingest - read per sample at TelemetryWriter:733.
        new(typeof(StorageOptions), StorageOptions.SectionName, nameof(StorageOptions.MinimumSampleIntervalSeconds), SettingGrade.Live, DisplaySubgroup: "Telemetry"),

        // Retention - TelemetryRetentionService reads these inside each sweep, and SweepInterval is
        // one hour.
        new(typeof(StorageOptions),
            StorageOptions.SectionName,
            nameof(StorageOptions.TelemetryRetentionDays),
            SettingGrade.Deferred,
            AppliesWhenKey: "Settings_AppliesOnNextSweep",
            DisplaySubgroup: "Telemetry"),
        new(typeof(StorageOptions),
            StorageOptions.SectionName,
            nameof(StorageOptions.EventRetentionDays),
            SettingGrade.Deferred,
            AppliesWhenKey: "Settings_AppliesOnNextSweep",
            DisplaySubgroup: "Telemetry"),
        new(typeof(StorageOptions),
            StorageOptions.SectionName,
            nameof(StorageOptions.MaxEventsPerPrinter),
            SettingGrade.Deferred,
            AppliesWhenKey: "Settings_AppliesOnNextSweep",
            DisplaySubgroup: "Telemetry"),

        // Sizes the writer's bounded channel at TelemetryWriter:234 and is read per batch at :547.
        new(typeof(StorageOptions), StorageOptions.SectionName, nameof(StorageOptions.WriteBatchSize), SettingGrade.Restart, DisplaySubgroup: "Telemetry"),

        // Builds a PeriodicTimer once when the writer's loop starts, at TelemetryWriter:462.
        new(typeof(StorageOptions), StorageOptions.SectionName, nameof(StorageOptions.WriteFlushIntervalSeconds), SettingGrade.Restart, DisplaySubgroup: "Telemetry"),

        // Cameras - all read at the point of use, in CameraFrameCache, CameraSnapshotFetcher and
        // CameraSourcePolicy.
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.RefreshFloorSeconds), SettingGrade.Live),
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.MaxAgeSeconds), SettingGrade.Live),
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.TimeoutSeconds), SettingGrade.Live),
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.MaxFrameBytes), SettingGrade.Live),
        new(typeof(CameraOptions), CameraOptions.SectionName, nameof(CameraOptions.WebRtcStunServer), SettingGrade.Live),

        // Asked about before it is turned on: it is the one setting here that makes this deployment
        // contact somebody else, and neither consequence is visible from the outcome.
        new(typeof(CameraOptions),
            CameraOptions.SectionName,
            nameof(CameraOptions.WebRtcStunEnabled),
            SettingGrade.Live,
            ConfirmOnEnableKey: "Settings_Confirm_Cameras_WebRtcStunEnabled"),

        // Mail. Every one of these is Restart, and the reason is three startup decisions rather than
        // any one property: Program picks SmtpEmailSender or LoggingEmailSender from
        // SmtpOptions.IsConfigured, registers TelemetryAlertService only when mail is configured, and
        // has AccountConfirmationPolicy capture the confirm-at-creation rule once. That last one is
        // deliberate and documented where it is built - whether a new account is auto-confirmed must
        // not change under a running deployment.
        // Naming a server here is what turns mail on, and what that changes lands at the next
        // restart rather than now - so it is asked about, and the answer says when it takes effect.
        new(typeof(SmtpOptions),
            SmtpOptions.SectionName,
            nameof(SmtpOptions.Host),
            SettingGrade.Restart,
            ConfirmOnEnableKey: "Settings_Confirm_Smtp_Host"),
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
