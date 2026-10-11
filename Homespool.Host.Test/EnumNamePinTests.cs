using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using AwesomeAssertions;

namespace Homespool.Host.Test;

/// <summary>
/// Every enum's member names, pinned: renaming, removing or adding a member fails here until the list
/// is updated.
/// </summary>
/// <remarks>
/// <para>
/// <b>A member's name is data once it leaves the process.</b> Enum columns hold the name as text, so
/// rows written before a rename stop reading back; the team capability and muted-notification columns
/// hold lists of names; and API responses, ProblemDetails and <c>/health</c> carry names through
/// <c>ToString()</c>. A rename in any of those needs the stored rows migrated or the clients told
/// before the list here changes.
/// </para>
/// <para>
/// <b>Every enum, not only the stored ones</b>, because no test can find every way a name leaves:
/// <c>ToString()</c> into a string property looks like any other string. Pinning all of them means
/// nobody has to judge whether a new enum is exported, and a judgement made once cannot go stale when
/// the enum later starts being sent. A harmless rename costs one line here.
/// </para>
/// <para>
/// Compared as sets, so reordering members changes nothing. The enums are the ones
/// <see cref="EnumConventionTests"/> checks, so the two tests agree on what "every enum" means.
/// </para>
/// </remarks>
public class EnumNamePinTests
{
    private static readonly Dictionary<string, string[]> Pinned = new(StringComparer.Ordinal)
    {
        ["Homespool.Host.Accounts.CredentialChange"] =
        [
            "Undefined", "ProviderLinked", "ProviderRemoved", "ProviderSwappedForPassword", "PasskeyAdded",
            "PasskeyRemoved", "PasskeyRevoked", "ApiTokenIssued",
        ],
        ["Homespool.Host.Accounts.UserAdminRefusal"] =
        [
            "Undefined", "None", "NoSuchAccount", "Self", "NotAnAdministrator",
        ],
        ["Homespool.Host.Authentication.ExternalRoundTrip"] = ["Undefined", "SignIn", "Link", "Reauthenticate"],
        ["Homespool.Host.Authentication.ExternalSignInResult"] =
        [
            "Undefined", "NoAccount", "NotAllowed", "RequiresSecondFactor", "Succeeded",
        ],
        ["Homespool.Host.Authentication.PasskeyCeremonyLedger.SpendResult"] =
        [
            "Undefined", "Spent", "AlreadySpent", "BeforeThisProcess", "Full",
        ],
        ["Homespool.Host.Authentication.SignInRefusal"] = ["Undefined", "Invalid", "LockedOut", "NotAllowed"],
        ["Homespool.Host.Authentication.StepUpRefusal"] = ["Undefined", "None", "WrongPassword", "LockedOut"],
        ["Homespool.Host.Cameras.LiveTransport"] = ["Undefined", "None", "Webrtc", "Mjpeg"],
        ["Homespool.Host.Cameras.MjpegFirstPartBuffer.PartKind"] = ["Undefined", "Complete", "Unframed", "EndOfStream"],
        ["Homespool.Host.Cameras.StreamRegistration"] =
        [
            "Undefined", "Registered", "SourceRefused", "ConfigurationNotSaved", "Unavailable",
        ],
        ["Homespool.Host.Cameras.StreamSyncOutcome"] =
        [
            "Undefined", "Registered", "Unchanged", "Removed", "Withheld", "SourceRefused",
            "ConfigurationNotSaved", "Unavailable",
        ],
        ["Homespool.Host.Cameras.WebRtcOfferOutcome"] = ["Undefined", "Answered", "CodecUnsupported", "Failed"],
        ["Homespool.Host.Certificates.AddressDurability"] =
        [
            "Undefined", "UntilTheLeaseMoves", "SurvivesALeaseChange", "ProbablyTheContainersOwn",
        ],
        ["Homespool.Host.Certificates.PrinterCertificateState"] =
        [
            "Undefined", "Ok", "NotInUse", "Missing", "ConfiguredAddressUncovered", "AddressesMoved",
            "LeafExpiring", "AuthorityExpiring", "ConfiguredAddressTooLong", "ConfiguredAddressResolvesToLoopback",
        ],
        ["Homespool.Host.Certificates.PublicCertificateProbeOutcome"] =
        [
            "Undefined", "Served", "Refused", "Unreachable",
        ],
        ["Homespool.Host.Configuration.SettingGrade"] = ["Undefined", "Live", "Deferred", "Restart"],
        ["Homespool.Host.Firmware.FirmwareFlashRefusal"] =
        [
            "Undefined", "NoSuchImage", "NotConnected", "Busy", "Queued", "AlreadyFlashing",
        ],
        ["Homespool.Host.Firmware.FirmwareFlashStage"] =
        [
            "Undefined", "Sending", "Arriving", "Restarting", "CleaningUp", "Done", "Failed",
        ],
        ["Homespool.Host.Firmware.FirmwareImageRefusal"] =
        [
            "Undefined", "NotAnImageName", "NotVerified", "WrongPrinter", "PrinterModelUnknown", "NameTaken",
            "OnAPrinter",
        ],
        ["Homespool.Host.Firmware.PrusaFirmwareVerdict"] =
        [
            "Undefined", "Verified", "NotAnImage", "Truncated", "Damaged", "NoSignature", "SignatureInvalid",
            "ResourcesUnreadable", "ResourcesChanged",
        ],
        ["Homespool.Host.Firmware.PrusaFirmwareVerifier.EntryKind"] =
        [
            "Undefined", "Tarball", "TarballDigest", "Image", "BlockSize", "BlockCount", "ImageHash",
        ],
        ["Homespool.Host.Health.AlertAction"] = ["Undefined", "None", "Alert", "Recovered"],
        ["Homespool.Host.Health.ExposureState"] =
        [
            "Undefined", "Ok", "PrinterTokensCrossThePublicInternet", "SessionInClear",
            "PrinterOnPlaintextWithoutNeedingIt",
        ],
        ["Homespool.Host.Listeners.ListenerClass"] = ["Undefined", "User", "Printer", "Transfer"],
        ["Homespool.Host.Mail.EmailSendResult"] = ["Undefined", "Sent", "NotConfigured", "Failed"],
        ["Homespool.Host.Notifications.DeliveryOutcome"] = ["Undefined", "Delivered", "Transient", "Gone", "Refused"],
        ["Homespool.Host.Notifications.NotificationUrgency"] = ["Undefined", "VeryLow", "Low", "Normal", "High"],
        ["Homespool.Host.Notifications.TestSendResult"] =
        [
            "Undefined", "Delivered", "Transient", "Gone", "Refused", "NotFound", "TooSoon",
        ],
        ["Homespool.Host.Notifications.WebPushSubscribeResult"] =
        [
            "Undefined", "Subscribed", "EndpointNotAllowed", "KeysInvalid", "TooMany", "EndpointTaken",
        ],
        ["Homespool.Host.Pages.PrinterFormFactor"] = ["Undefined", "Open", "Enclosed"],
        ["Homespool.Host.Pages.Printers.HeaterState"] =
        [
            "Undefined", "Unknown", "Off", "Cooling", "Heating", "AtTarget", "Measured",
        ],
        ["Homespool.Host.Pages.Printers.QueueEntryStatus"] = ["Undefined", "Waiting", "Sending", "Held"],
        ["Homespool.Host.Pages.StatusKind"] = ["Undefined", "Success", "Info", "Warning", "Danger"],
        ["Homespool.Host.PrintFiles.PrintFileDeletion"] = ["Undefined", "NotFound", "Deleted", "Queued"],
        ["Homespool.Host.Printing.CommandSendOutcome"] =
        [
            "Undefined", "Completed", "Dispatched", "NotConnected", "AlreadyInFlight", "ResponseTimedOut",
            "SendTimedOut",
        ],
        ["Homespool.Host.Printing.OutdatedCopyRemoval"] =
        [
            "Undefined", "NothingToRemove", "Removed", "InUse", "Refused",
        ],
        ["Homespool.Host.Printing.TransferSlot"] = ["Undefined", "Unreported", "Free", "Taken"],
        ["Homespool.Host.PrusaConnect.CommandHandover"] = ["Undefined", "Written", "Parked"],
        ["Homespool.Host.PrusaConnect.Commands.DialogResponse"] =
        [
            "Undefined", "Abort", "AbortInvalidateTest", "Adjust", "All", "Always", "Back", "Calibrate", "Cancel",
            "Help", "Change", "Continue", "Cooldown", "Disable", "Done", "Filament", "FilamentRemoved", "Finish",
            "FSDisable", "Ignore", "Left", "Load", "MMUDisable", "Never", "Next", "No", "NotNow", "Ok", "Pause",
            "Print", "PurgeMore", "Quit", "Reheat", "Replace", "Remove", "Restart", "Resume", "Retry", "Right",
            "Skip", "Slowly", "SpoolJoin", "Stop", "Unload", "Yes", "Heatup", "Postpone5Days", "PRINT", "Tool1",
            "Tool2", "Tool3", "Tool4", "Tool5",
        ],
        ["Homespool.Host.PrusaConnect.Commands.PropertyName"] =
        [
            "Undefined", "HostName", "EnclosureEnabled", "EnclosurePrintingFiltration", "EnclosurePostPrint",
            "EnclosurePostPrintFiltrationTime", "NozzleDiameter", "NozzleHighFlow", "NozzleHardened",
            "ChamberTargetTemp", "ChamberFanPwmTarget", "AddonPower", "ChamberLedIntensity",
        ],
        ["Homespool.Host.PrusaConnect.Enums.WireComplaint"] =
        [
            "Undefined", "UnreadableMessage", "UnreadableInfo", "NonFiniteNumbers", "BodyTooLarge",
            "InlineTransferOverHttp", "OverMessageBudget", "UnknownEventWord",
        ],
        ["Homespool.Host.PrusaConnect.JsonDocumentScanner.Mode"] =
        [
            "Undefined", "BeforeDocument", "Structure", "InString", "InStringEscape", "Slash", "LineComment",
            "BlockComment", "BlockCommentStar",
        ],
        ["Homespool.Host.PrusaConnect.PrinterTransport"] = ["Undefined", "WebSocket", "Http"],
        ["Homespool.Host.Queue.JobAnswer"] =
        [
            "Undefined", "NotAsked", "Ours", "SomebodyElses", "NoJob", "Inconclusive",
        ],
        ["Homespool.Host.Queue.PrintStartVerdict"] =
        [
            "Undefined", "KeepWaiting", "Started", "NeverStarted", "Unresolvable",
        ],
        ["Homespool.Host.Queue.QueueActionKind"] = ["Undefined", "Nothing", "Transfer", "Print", "Wait"],
        ["Homespool.Host.Queue.QueueCancellation"] = ["Undefined", "NotFound", "Removed", "StopRequested"],
        ["Homespool.Host.Queue.QueueWaitReason"] =
        [
            "Undefined", "Transferring", "InsufficientSpace", "AwaitingPrinterPath", "IncompatibleWithPrinter",
            "PrinterNotAvailable", "PrintStarting", "PrintStartUnresolved", "PrinterBusy", "TransferRetrying",
            "TransferRefused", "QueuerLostAccess", "FileUnreadable", "TransferStopped", "TransferAborted",
            "TransferAbortRetrying", "FileTooLarge", "FirmwareInstalling", "PrintRetrying", "PrintRefused",
            "PrinterPathUnknown", "TransferSlotTaken", "TransferSlotUnreported",
        ],
        ["Homespool.Model.Capability"] =
        [
            "Undefined", "ViewPrinter", "ViewQueue", "ViewHistory", "ViewCamera", "Print", "ControlPrinter",
            "ManagePrinter", "ManageCamera", "ViewOwnFiles", "UploadOwnFiles", "ManipulateOwnFiles",
            "ViewAccountDetails",
        ],
        ["Homespool.Model.FileType"] = ["Undefined", "GCode", "PrusaFirmware"],
        ["Homespool.Model.InvitationType"] = ["Undefined", "Signup", "Recovery"],
        ["Homespool.Model.JobStatus"] = ["Undefined", "Printing", "Paused", "Finished", "Error", "Stopped"],
        ["Homespool.Model.LimitedAction"] =
        [
            "Undefined", "ClaimPrinter", "RemovePrinter", "DisableTwoFactor", "SendPasswordResetEmail",
            "SendConfirmationEmail", "AddPasskey", "StepUp", "ChangeEmail", "SendVerificationEmail",
            "ChangeSignIn",
        ],
        ["Homespool.Model.NotificationChannelKind"] = ["Undefined", "WebPush"],
        ["Homespool.Model.NotificationKind"] =
        [
            "Undefined", "PrinterNeedsAttention", "PrintFinished", "PrintDidNotFinish", "QueueHeld",
            "FilamentChangeSoon", "PrinterLost", "ServiceHealth",
        ],
        ["Homespool.Model.PrintCompatibilityFinding"] =
        [
            "Undefined", "AbrasiveFilamentNeedsHardenedNozzle", "AbrasiveFilamentMayUseASoftNozzle",
            "IncompatiblePrinterModel", "NozzleDiameterMismatch", "HighFlowNozzleRequired",
        ],
        ["Homespool.Model.PrintCompatibilitySeverity"] = ["Undefined", "Warn", "Hold"],
        ["Homespool.Model.PrintFileMetadataState"] = ["Undefined", "Unread", "Unreadable", "Silent", "Read"],
        ["Homespool.Model.PrintHoldReason"] =
        [
            "Undefined", "InsufficientSpace", "FileExistsDifferentSize", "FileExistsUnknownSize",
            "AbrasiveFilamentNeedsHardenedNozzle", "IncompatiblePrinterModel", "PrintStartUnresolved",
            "TransferRefused", "FileUnreadable", "TransferStopped", "TransferAborted", "FileTooLarge",
            "PrintRefused", "PrinterPathUnknown",
        ],
        ["Homespool.Model.PrintState"] =
        [
            "Undefined", "Unconfirmed", "Starting", "Printing", "Finished", "Stopped", "Failed", "Unknown",
        ],
        ["Homespool.Model.PrinterEventType"] =
        [
            "Undefined", "Accepted", "Rejected", "Failed", "Finished", "Info", "StateChanged", "StorageEjected",
            "StorageInserted", "FileChanged", "FileInfo", "JobInfo", "TransferInfo", "MeshBedData",
            "TransferAborted", "TransferStopped", "TransferFinished", "SlotEvent", "CancelableChanged",
        ],
        ["Homespool.Model.PrinterModelGroup"] =
        [
            "Undefined", "Unknown", "Mk3", "Mk3_5", "Mk4", "Mk4S", "Mini", "Xl", "Xlp", "Ix", "CoreOne",
            "CoreOneL", "CoreOneIndx", "CoreOneLIndx",
        ],
        ["Homespool.Model.PrinterStatus"] =
        [
            "Undefined", "Unknown", "Idle", "Busy", "Printing", "Paused", "Finished", "Stopped", "Error",
            "Attention", "Ready", "Manipulating", "Offline",
        ],
        ["Homespool.Model.PrinterType"] = ["Undefined", "PrusaConnect"],
    };

    [Fact]
    public void EveryEnumsMemberNamesAreThePinnedOnes()
    {
        // Arrange
        Dictionary<string, Type> loaded = EnumConventionTests.LoadedEnums().ToDictionary(KeyOf, StringComparer.Ordinal);

        // Act
        List<string> problems = [];

        foreach ((string key, Type type) in loaded.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            string[] current = NamesOf(type);

            if (!Pinned.TryGetValue(key, out string[]? pinned))
            {
                problems.Add($"{key} is not pinned. Add its line: {LineFor(key, current)}");
                continue;
            }

            string[] gone = [.. pinned.Except(current, StringComparer.Ordinal)];
            string[] added = [.. current.Except(pinned, StringComparer.Ordinal)];

            if (gone.Length > 0)
            {
                string change = added.Length > 0 ?
                    $"{string.Join(", ", gone)} gone, {string.Join(", ", added)} new" :
                    $"{string.Join(", ", gone)} gone";

                problems.Add($"{key}: {change}. If database rows or API clients hold a gone name, migrate the rows " +
                             $"or tell the clients first. Then make its line: {LineFor(key, current)}");
            }
            else if (added.Length > 0)
            {
                problems.Add($"{key}: {string.Join(", ", added)} new. Make its line: {LineFor(key, current)}");
            }
        }

        foreach (string key in Pinned.Keys.Where(key => !loaded.ContainsKey(key)).Order(StringComparer.Ordinal))
        {
            problems.Add($"{key} is pinned and no longer exists. If database rows or API clients hold its names, " +
                         "migrate the rows or tell the clients first. Then remove its line.");
        }

        // Assert
        loaded.Should().NotBeEmpty("if no enum is found, nothing here is being checked");

        problems.Should().BeEmpty();
    }

    private static string KeyOf(Type type)
    {
        return type.FullName!.Replace('+', '.');
    }

    private static string[] NamesOf(Type type)
    {
        return [.. type.GetFields(BindingFlags.Public | BindingFlags.Static).Select(field => field.Name)];
    }

    private static string LineFor(string key, string[] names)
    {
        return $"[\"{key}\"] = [{string.Join(", ", names.Select(name => $"\"{name}\""))}],";
    }
}
