using System.Collections.Generic;
using System.Net;

namespace Homespool.Host.Cameras;

/// <summary>
/// What this process can learn about the machine it runs on: its name and its interface addresses.
/// </summary>
/// <remarks>
/// <para>
/// <b>An interface so that no test of a camera save depends on the machine running it.</b> Both
/// answers feed <see cref="CameraSourcePolicy"/>'s "this server itself" refusal, which every save
/// goes through - so read straight from the platform, a machine that happens to own a fixture's
/// address, or to be named like a fixture's host, refuses a camera the test expects to be accepted.
/// </para>
/// <para>
/// <b>An empty answer narrows the check without failing it</b>, which is the right behaviour on a
/// platform that will not answer and the wrong one anywhere else. Only
/// <see cref="PlatformLocalMachine"/> is registered by the application.
/// </para>
/// </remarks>
public interface ILocalMachine
{
    /// <summary>
    /// This machine's host name, or <see langword="null"/> when it cannot name itself.
    /// </summary>
    string? HostName();

    /// <summary>
    /// The unicast addresses held by this machine's interfaces that are up, loopback excluded.
    /// </summary>
    /// <remarks>
    /// Read on every call rather than once: an address can be gained or lost while the process runs.
    /// </remarks>
    IReadOnlyList<IPAddress> Addresses();
}
