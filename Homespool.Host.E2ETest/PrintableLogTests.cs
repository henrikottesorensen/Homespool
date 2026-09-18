using System;
using System.Net.Http;
using System.Threading.Tasks;

using AwesomeAssertions;

namespace Homespool.Host.E2ETest;

/// <summary>
/// The host's logger makes every property printable - shown on a line nothing in this repository
/// writes, because that is the case only a rule in the logging pipeline can reach.
/// </summary>
/// <remarks>
/// <para>
/// Serilog's request middleware logs the path as the server decoded it, so an encoded newline in a
/// URL is a newline in the property, from a request nobody has authenticated. There is no call site
/// of ours to clean it at. What the rule does to a value is pinned by
/// <c>PrintableLogEnricherTests</c>; this pins that the host registers it, which those cannot see.
/// </para>
/// <para>
/// The sink here sits where every sink does, after the enrichers - so what it captures is what a
/// console, a file or anything an operator configures would have been handed.
/// </para>
/// </remarks>
public sealed class PrintableLogTests : IAsyncLifetime
{
    private readonly ScratchDirectory _scratch = ScratchDirectory.Create("e2e-printable-log");
    private readonly CapturingSink _logs = new();
    private HomespoolFactory _factory = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new HomespoolFactory(_scratch, null, _logs);

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();

        _scratch.Dispose();
    }

    [Fact]
    public async Task TheRequestLineCarriesADecodedPathMadePrintable()
    {
        // Arrange
        using HttpClient client = _factory.CreateClient();

        // Act - a newline, a terminal escape and a right-to-left override, percent-encoded as any
        // client can send them
        await client.GetAsync("/nowhere/a%0Ab%1Bc%E2%80%AEd", TestContext.Current.CancellationToken);

        // Assert - waited for, because the middleware writes its line after the response has gone
        bool logged = await FakePrinterConnections.WaitUntilAsync(
            () => _logs.HasEventWith(("RequestPath", "/nowhere/a\uFFFDb\uFFFDc\uFFFDd")),
            TimeSpan.FromSeconds(5));

        logged.Should().BeTrue("the request line is a log event like any other, and its path a property like any other");
    }
}
