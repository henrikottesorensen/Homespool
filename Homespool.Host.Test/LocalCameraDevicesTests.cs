using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Host.Cameras;

namespace Homespool.Host.Test;

/// <summary>
/// Reading attached cameras out of a by-id directory, pointed at one the test builds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Real symlinks, dangling on purpose.</b> In the container the entries point at <c>/dev/videoN</c>
/// nodes that do not exist there - the devices are passed to the sidecar only - so a lister that
/// followed its links would find nothing. These point at nodes that do not exist anywhere, which is
/// the same situation on any machine the suite runs on.
/// </para>
/// <para>
/// The names are the C910 on the rig and a camera that reports its own strings, each with the
/// metadata node UVC puts beside every capture node.
/// </para>
/// </remarks>
public sealed class LocalCameraDevicesTests : IDisposable
{
    private const string C910 = "usb-046d_0821_437242E0-video-index0";
    private const string Brio = "usb-Logitech_BRIO_5A3B-video-index0";

    private readonly DirectoryInfo _byId = Directory.CreateTempSubdirectory("by-id-");

    public LocalCameraDevicesTests()
    {
        Link(C910, "../../video0");
        Link("usb-046d_0821_437242E0-video-index1", "../../video1");
        Link(Brio, "../../video2");
        Link("usb-Logitech_BRIO_5A3B-video-index1", "../../video3");
    }

    public void Dispose()
    {
        _byId.Delete(recursive: true);
    }

    /// <summary>
    /// Every capture node in the configured directory is listed, and the metadata nodes beside them
    /// are not.
    /// </summary>
    [Fact]
    public void TheCaptureNodesInTheConfiguredDirectoryAreListed()
    {
        IReadOnlyList<LocalCameraDevice> devices = Build(_byId.FullName).List();

        devices.Select(device => device.Name).Should().BeEquivalentTo([C910, Brio]);
    }

    /// <summary>
    /// A device's node is what its link says, read without following it - the node does not exist
    /// here, as it does not in the container.
    /// </summary>
    [Fact]
    public void ANodeIsReadFromTheLinkWithoutFollowingIt()
    {
        LocalCameraDevices devices = Build(_byId.FullName);

        devices.NodeFor(C910).Should().Be("video0");
        devices.NodeFor(Brio).Should().Be("video2");
    }

    /// <summary>
    /// A name the directory does not hold has no node, rather than an error.
    /// </summary>
    [Fact]
    public void ANameNotInTheDirectoryHasNoNode()
    {
        Build(_byId.FullName).NodeFor("usb-0000_0000-video-index0").Should().BeNull();
    }

    /// <summary>
    /// A directory that is not there lists no cameras: most machines have none attached, and Docker
    /// creates the mount empty when the host has no <c>/dev/v4l</c> at all.
    /// </summary>
    [Fact]
    public void AMissingDirectoryListsNothing()
    {
        Build(Path.Combine(_byId.FullName, "absent")).List().Should().BeEmpty();
    }

    private static LocalCameraDevices Build(string directory)
    {
        UsbDeviceNames usbNames = new(
            NullLogger<UsbDeviceNames>.Instance,
            new List<string> { Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.ids") });

        return new LocalCameraDevices(NullLogger<LocalCameraDevices>.Instance,
                                      usbNames,
                                      TestOptions.Monitor(new CameraOptions { LocalDeviceDirectory = directory }));
    }

    private void Link(string name, string target)
    {
        File.CreateSymbolicLink(Path.Combine(_byId.FullName, name), target);
    }
}
