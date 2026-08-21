using AwesomeAssertions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

using Homespool.Host.Cameras;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// That a camera's password is not in the row, and comes back when the stream server needs it.
/// </summary>
/// <remarks>
/// An ephemeral provider rather than the real key ring: these assert the shape of what is stored and
/// that the round trip holds, neither of which depends on where the keys live. What the protection
/// is worth against a given threat is argued in <c>CameraCredentialProtector</c>'s own remarks and is
/// not a thing a test can pin.
/// </remarks>
public sealed class CameraCredentialProtectorTests
{
    private static CameraCredentialProtector Protector()
    {
        return new CameraCredentialProtector(new EphemeralDataProtectionProvider(),
                                             NullLogger<CameraCredentialProtector>.Instance);
    }

    [Fact]
    public void ThePasswordDoesNotSurviveIntoTheStoredColumns()
    {
        // The whole point: what is written down must not contain the secret.
        CameraCredential stored = Protector().Split("rtsp://admin:hunter2@192.168.1.50/live");

        stored.Address.Should().Be("rtsp://192.168.1.50/live");
        stored.User.Should().Be("admin");
        stored.Secret.Should().NotBeNull();
        stored.Secret.Should().NotContain("hunter2");
        stored.Address.Should().NotContain("hunter2");
    }

    [Fact]
    public void TheStreamServerStillGetsTheRealSource()
    {
        CameraCredentialProtector protector = Protector();
        CameraCredential stored = protector.Split("rtsp://admin:hunter2@192.168.1.50/live");

        Camera camera = new()
        {
            Source = stored.Address,
            CredentialUser = stored.User,
            CredentialSecret = stored.Secret,
        };

        protector.Reveal(camera).Should().Be("rtsp://admin:hunter2@192.168.1.50/live");
    }

    [Fact]
    public void ACameraNeedingNoCredentialStoresNoSecret()
    {
        CameraCredential stored = Protector().Split("rtsp://192.168.1.50/live");

        stored.User.Should().BeNull();
        stored.Secret.Should().BeNull();
        stored.Address.Should().Be("rtsp://192.168.1.50/live");
    }

    [Fact]
    public void ARowWrittenBeforeTheSplitIsUsedAsItStands()
    {
        // The upgrade path, and the whole of it: no secret means the credential is still inline, and
        // the next save splits it out.
        Camera legacy = new() { Source = "rtsp://admin:hunter2@192.168.1.50/live" };

        Protector().Reveal(legacy).Should().Be("rtsp://admin:hunter2@192.168.1.50/live");
    }

    [Fact]
    public void APasswordThisDeploymentCannotDecryptDoesNotTakeTheOthersDown()
    {
        // A lost key ring degrades to "this camera does not stream", which somebody fixes by typing
        // the password again - rather than to an exception that stops the reconciler mid-sweep.
        Camera foreign = new()
        {
            Source = "rtsp://192.168.1.50/live",
            CredentialUser = "admin",
            CredentialSecret = "not-ciphertext-this-deployment-can-read",
        };

        Protector().Reveal(foreign).Should().Be("rtsp://192.168.1.50/live");
    }

    [Fact]
    public void TheCiphertextIsNonDeterministic()
    {
        // Two saves of the same password differ, so the column cannot be used to tell whether two
        // cameras share a password.
        CameraCredentialProtector protector = Protector();

        string? first = protector.Split("rtsp://admin:hunter2@192.168.1.50/live").Secret;
        string? second = protector.Split("rtsp://admin:hunter2@192.168.1.50/live").Secret;

        first.Should().NotBe(second);
    }
}
