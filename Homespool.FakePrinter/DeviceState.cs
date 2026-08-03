namespace Homespool.FakePrinter;

/// <summary>
/// The firmware's own device states - <c>printer_state::DeviceState</c>
/// (Prusa-Firmware-Buddy <c>src/state/printer_state.hpp:21-32</c> at the pinned ref), whose
/// <c>to_str</c> (<c>printer_state.cpp:563-587</c>) produces the wire spellings. The Python SDK's
/// <c>State</c> enum agrees on nine of them; the firmware additionally has
/// <see cref="Unknown"/>.
/// </summary>
public enum DeviceState
{
    Undefined = 0,

    /// <summary>Powered on, nothing happening.</summary>
    Idle,

    /// <summary>Occupied by something that is not a print job (e.g. a filament change).</summary>
    Busy,

    /// <summary>A print job is running.</summary>
    Printing,

    /// <summary>A print job is paused and resumable.</summary>
    Paused,

    /// <summary>The last job completed; the finished screen is showing.</summary>
    Finished,

    /// <summary>The last job was stopped; the stopped screen is showing.</summary>
    Stopped,

    /// <summary>An error screen. Commands are rejected wholesale in this state.</summary>
    Error,

    /// <summary>The printer wants the user's attention (e.g. filament runout).</summary>
    Attention,

    /// <summary>Explicitly marked ready for the next queued job.</summary>
    Ready,

    /// <summary>
    /// The firmware's fallback: <c>to_str</c>'s default arm renders it as <c>"UNKNOWN"</c>, so a
    /// real printer <em>can</em> put that on the wire, while the SDK treats it as server-side only
    /// and this server's telemetry parsing rejects it. Never entered by the fake's own transitions -
    /// it exists for <see cref="FakeDevice.ForceState"/> to make that disagreement testable.
    /// </summary>
    Unknown,
}
