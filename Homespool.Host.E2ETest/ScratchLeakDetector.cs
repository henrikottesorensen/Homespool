using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

[assembly: AssemblyFixture(typeof(Homespool.Host.E2ETest.ScratchLeakDetector))]

namespace Homespool.Host.E2ETest;

/// <summary>
/// Fails the run if any test finished without removing its <see cref="ScratchDirectory"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing else can see this.</b> A test that leaves its directory behind passes, its class
/// passes, and the suite passes; the only symptom is temporary files accumulating. Where
/// <c>/tmp</c> is disk that is invisible litter, and where it is tmpfs it is resident memory that
/// outlives the run - enough runs and the machine is out of memory, which is how it was eventually
/// found, from a suite reporting <c>Passed!</c> over a SIGKILL.
/// </para>
/// <para>
/// <b>The defect this exists to catch is disposal that is never called</b>, which is not the same as
/// disposal that is wrong. Five classes implemented <c>IAsyncLifetime</c> and <c>IDisposable</c> with
/// a <c>DisposeAsync</c> that returned without ever reaching <c>Dispose</c> - so their cleanup was
/// correct, reviewed, and dead. Reading those methods missed it twice; only what was left on disk
/// showed it. So this asserts on what happened, not on what the code says.
/// </para>
/// <para>
/// <b>An assembly fixture rather than a test, because a test cannot see the end of the run.</b>
/// Classes execute in parallel, so at any moment during the suite some scratch directories are legitimately
/// alive and a count proves nothing - an earlier attempt here counted them mid-run and would have
/// had to tolerate so many that the real five-class leak of seventeen passed straight through it.
/// Disposed after every test, this can require <b>zero</b> and mean it, which is what makes a single
/// mis-wired class fail the run rather than needing a systemic collapse.
/// </para>
/// </remarks>
public sealed class ScratchLeakDetector : IDisposable
{
    [SuppressMessage("Design", "CA1065:Do not raise exceptions in unexpected locations",
                     Justification =
                         "Throwing is the whole mechanism. An assembly fixture's disposal is the only point that runs after every test, and raising there is how xUnit is told the run failed - a leak reported any other way would be a message in a log nobody reads.")]
    public void Dispose()
    {
        IReadOnlyCollection<string> outstanding = ScratchDirectory.Outstanding;

        if (outstanding.Count == 0)
        {
            return;
        }

        // The console reports an assembly cleanup failure by exception type alone - "Passed!" on the
        // summary line, exit code 1, and nothing saying what leaked. The message survives in the
        // .trx, which is where it has to be read from; writing it to stdout as well does not work,
        // because the test host's output is not forwarded here.
        string detail =
            $"{outstanding.Count} test scratch(s) were never disposed, so their databases and "
            + "content roots are still on disk: "
            + string.Join(", ", outstanding.OrderBy(name => name, StringComparer.Ordinal).Distinct())
            + ". The usual cause is a class implementing IAsyncLifetime whose DisposeAsync returns "
            + "without calling Dispose - xUnit calls the asynchronous one, so the Dispose body never "
            + "runs however correct it looks.";

        throw new InvalidOperationException(detail);
    }
}
