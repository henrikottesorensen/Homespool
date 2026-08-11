using System;
using System.Collections.Generic;
using System.IO;

using AwesomeAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// What an unnamed camera calls itself.
/// </summary>
/// <remarks>
/// <para>
/// The table is written by the test rather than read from the machine: <c>usb.ids</c> is installed
/// in the container image but not necessarily on whatever runs these, and a suite that passes only
/// where a package happens to be present tests the agent as much as the code.
/// </para>
/// <para>
/// The device names here are real shapes, not invented ones — <c>046d_0821_437242E0</c> is the C910
/// on the rig, and the mixed form beside it is what udev writes when hardware reports a product
/// string but no manufacturer string.
/// </para>
/// </remarks>
public sealed class CameraDisplayNameTests : IDisposable
{
    /// <summary>
    /// A cut of <c>usb.ids</c> in its real format: two-space separator, tab-indented products, and a
    /// doubly-indented interface line that must not be mistaken for one.
    /// </summary>
    private const string Table = """
                                 #
                                 # A comment, which the parser skips.
                                 #
                                 046d  Logitech, Inc.
                                 \t0821  HD Webcam C910
                                 \t0825  Webcam C270
                                 \t\t00  An interface, not a product
                                 0c45  Microdia
                                 \t6340  Camera
                                 1bcf  Sunplus Innovation Technology Inc.
                                 \t2c99  Cheap Webcam
                                 """;

    private readonly string _tablePath;

    public CameraDisplayNameTests()
    {
        _tablePath = Path.Combine(Path.GetTempPath(), $"usb-ids-{Guid.NewGuid():N}.ids");

        // Written with real tabs: usb.ids is tab-indented, and the parser reads that indentation to
        // tell a product from a vendor.
        File.WriteAllText(_tablePath, Table.Replace("\\t", "\t", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        File.Delete(_tablePath);
    }

    /// <summary>
    /// The shapes udev actually produces, and what each should read as.
    /// </summary>
    /// <remarks>
    /// In order: both ids numeric, which is a camera that reported no strings at all; the same
    /// without a serial, so there is nothing to put in parentheses; a numeric vendor beside a
    /// product string, where only the leading id needs translating; a name udev fully resolved, for
    /// which the table is never consulted; and a vendor the table does not list, which keeps what
    /// udev said.
    /// </remarks>
    [Theory]
    [InlineData("usb-046d_0821_437242E0-video-index0", "Logitech HD Webcam C910 (437242E0)")]
    [InlineData("usb-046d_0825_A1B2-video-index0", "Logitech Webcam C270 (A1B2)")]
    [InlineData("usb-0c45_6340-video-index0", "Microdia Camera")]
    [InlineData("usb-046d_HD_Pro_Webcam_C920_2A3B-video-index0", "Logitech HD Pro Webcam C920 2A3B")]
    [InlineData("usb-Logitech_BRIO_9F2C-video-index0", "Logitech BRIO 9F2C")]
    [InlineData("usb-ffff_eeee_D4-video-index0", "ffff eeee D4")]
    public void AnAttachedCameraIsNamedAfterItsHardware(string deviceName, string expected)
    {
        CameraDisplayNames names = Build();
        Camera camera = new() { Uuid = Guid.NewGuid(), Source = LocalCameraDevices.SourceFor(deviceName) };

        names.For(camera).Should().Be(expected);
    }

    /// <summary>
    /// The corporate form in <c>usb.ids</c> is not how anybody refers to the thing.
    /// </summary>
    [Fact]
    public void AVendorsLegalSuffixIsDropped()
    {
        CameraDisplayNames names = Build();
        Camera camera = new() { Uuid = Guid.NewGuid(), Source = LocalCameraDevices.SourceFor("usb-1bcf_2c99-video-index0") };

        names.For(camera).Should().Be("Sunplus Innovation Technology Cheap Webcam");
    }

    /// <summary>
    /// A name somebody typed wins outright — that is the whole reason the column stays null until
    /// they do.
    /// </summary>
    [Fact]
    public void AChosenNameBeatsTheHardware()
    {
        CameraDisplayNames names = Build();
        Camera camera = new()
        {
            Uuid = Guid.NewGuid(),
            Name = "Workshop",
            Source = LocalCameraDevices.SourceFor("usb-046d_0821_437242E0-video-index0"),
        };

        names.For(camera).Should().Be("Workshop");
    }

    /// <summary>
    /// A network camera describes nothing about itself, so the uuid is all that is left - and the
    /// caller's own last resort is preferred to it when one is offered.
    /// </summary>
    [Fact]
    public void AnUnnamedNetworkCameraFallsBackToTheUuid()
    {
        CameraDisplayNames names = Build();
        Camera camera = new() { Uuid = Guid.NewGuid(), Source = "rtsp://192.168.13.217/live" };

        names.For(camera).Should().Be(camera.Uuid.ToString());
        names.For(camera, "Camera 2").Should().Be("Camera 2");
    }

    /// <summary>
    /// A last resort is exactly that: it must not displace anything real.
    /// </summary>
    [Fact]
    public void ALastResortNeverDisplacesAName()
    {
        CameraDisplayNames names = Build();
        Camera attached = new() { Uuid = Guid.NewGuid(), Source = LocalCameraDevices.SourceFor("usb-0c45_6340-video-index0") };
        Camera chosen = new() { Uuid = Guid.NewGuid(), Name = "Workshop", Source = "rtsp://192.168.13.217/live" };

        names.For(attached, "Camera 2").Should().Be("Microdia Camera");
        names.For(chosen, "Camera 2").Should().Be("Workshop");
    }

    /// <summary>
    /// The table is absent on a developer's machine and in any image built before it was added.
    /// Naming has to degrade to udev's own name rather than fail.
    /// </summary>
    [Fact]
    public void AMissingTableLeavesUdevsOwnName()
    {
        CameraDisplayNames names = Build(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.ids"));
        Camera camera = new()
            { Uuid = Guid.NewGuid(), Source = LocalCameraDevices.SourceFor("usb-046d_0821_437242E0-video-index0") };

        names.For(camera).Should().Be("046d 0821 437242E0");
    }

    private CameraDisplayNames Build(string? tablePath = null)
    {
        UsbDeviceNames usbNames = new(
            NullLogger<UsbDeviceNames>.Instance,
            new List<string> { tablePath ?? _tablePath });

        return new CameraDisplayNames(new LocalCameraDevices(NullLogger<LocalCameraDevices>.Instance, usbNames));
    }
}
