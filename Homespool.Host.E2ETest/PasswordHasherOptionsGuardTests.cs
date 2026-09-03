using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Homespool.Host.E2ETest;

/// <summary>
/// Guards the one assumption <see cref="Host.Accounts.PasswordVerificationDecoy"/> rests on: that
/// nothing configures <see cref="PasswordHasherOptions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The decoy's hash is built with <see cref="PasswordHasher{TUser}"/>'s defaults, and a PBKDF2
/// verification costs whatever the <i>stored</i> hash's embedded iteration count says. While the
/// application leaves those options alone, the decoy and a real account cost the same and the timing
/// channel stays shut. Raise the iteration count and real accounts get slower while the decoy does
/// not - reopening the channel silently, because every message-level test would stay green and the
/// difference lives in wall-clock time nothing asserts on.
/// </para>
/// <para>
/// <b>So the guard is here rather than a comment.</b> It reads the options out of a real host rather
/// than grepping for a call, so it catches the setting arriving by any route - a configuration file
/// binding included.
/// </para>
/// </remarks>
public sealed class PasswordHasherOptionsGuardTests : IAsyncLifetime, IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"ps-hashopt-{Guid.NewGuid():N}.db");
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory($"Data Source={_databasePath}");
        _ = _factory.Server;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    public void Dispose()
    {
        foreach (string path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ThePasswordHasherIsLeftAtItsDefaults()
    {
        // Arrange
        PasswordHasherOptions defaults = new();

        // Act
        using IServiceScope scope = _factory.Services.CreateScope();
        PasswordHasherOptions configured =
            scope.ServiceProvider.GetRequiredService<IOptions<PasswordHasherOptions>>().Value;

        // Assert
        configured.IterationCount.Should().Be(
            defaults.IterationCount,
            "PasswordVerificationDecoy hashes with the defaults, so a configured iteration count would "
            + "make a real account cost more than the decoy and reopen the sign-in timing channel");

        configured.CompatibilityMode.Should().Be(defaults.CompatibilityMode,
                                                 "the same reasoning - it changes what a verification costs");
    }
}
