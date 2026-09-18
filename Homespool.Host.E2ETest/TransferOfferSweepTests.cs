using System;
using System.IO;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Homespool.Host.PrusaConnect.Transfers;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The running application closes an abandoned transfer offer by itself, with nobody sending
/// another file.
/// </summary>
/// <remarks>
/// <para>
/// <b>Through the real host, because the registration is the thing under test.</b> The store's own
/// tests call <see cref="TransferOfferStore.SweepIdle"/> directly, and would all stay green with the
/// hosted service unregistered - which is precisely the defect the service exists to fix: a sweep
/// that is correct and that nothing calls.
/// </para>
/// <para>
/// <b>The clock is advanced a step at a time until the offer goes</b>, rather than once past the
/// limit. A hosted service's <c>ExecuteAsync</c> is scheduled onto the pool when the host starts,
/// so its timer may not exist yet when the test first moves the clock, and a timer created after
/// the jump would be waiting for a tick that a single advance never delivers.
/// </para>
/// </remarks>
public sealed class TransferOfferSweepTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("offersweep");

    // Started at the real time, not the fake's default of 2000: the whole host reads this clock,
    // and a certificate or a token minted in 2000 is a different test.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    private HomespoolFactory _root = null!;
    private WebApplicationFactory<Controllers.PrinterAppController> _factory = null!;

    public ValueTask InitializeAsync()
    {
        _root = new HomespoolFactory(_scratch);

        _factory = _root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
        }));

        _ = _factory.Server;

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task AnOfferNoPrinterCollectsIsClosedWithoutAnotherSend()
    {
        // Arrange
        TransferOfferStore store = _factory.Services.GetRequiredService<TransferOfferStore>();

        TaskCompletionSource<string> retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Retired += token => retired.TrySetResult(token);

        string path = Path.Combine(_scratch.Path, "abandoned.gcode");
        await File.WriteAllBytesAsync(path, new byte[64], TestContext.Current.CancellationToken);

        store.Offer("abandoned", path, printerId: 1).Should().BeTrue();

        // Act
        DateTime giveUpAt = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (!retired.Task.IsCompleted && DateTime.UtcNow < giveUpAt)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));

            await Task.WhenAny(retired.Task, Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
        }

        // Assert
        retired.Task.IsCompleted.Should().BeTrue("the hosted sweep is the only thing here that could have closed it");
        (await retired.Task).Should().Be("abandoned");
    }
}
