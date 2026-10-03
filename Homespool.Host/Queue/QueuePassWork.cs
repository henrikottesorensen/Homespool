using System;
using System.Threading;
using System.Threading.Tasks;

namespace Homespool.Host.Queue;

/// <summary>
/// One pass's claim on the <see cref="QueueWorkBudget"/>: held while the pass works, handed back
/// while it waits on a printer, and always given up when the pass's scope ends.
/// </summary>
/// <remarks>
/// Scoped, so that every method of a pass can reach it through the scope it already carries. Used by
/// one pass, one step at a time, so it holds no lock of its own.
/// </remarks>
public sealed class QueuePassWork(QueueWorkBudget budget) : IDisposable
{
    private bool _holding;

    /// <summary>Waits for a permit; the pass does not start before it has one.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task BeginAsync(CancellationToken cancellationToken)
    {
        await budget.EnterAsync(cancellationToken);

        _holding = true;
    }

    /// <summary>Waits on a printer without holding a permit, and takes one again once it has answered.</summary>
    /// <typeparam name="T">What the printer's answer comes back as.</typeparam>
    /// <param name="wait">Starts the wait: a command to the printer, or an offer to it.</param>
    /// <returns>Whatever the wait comes back with, or throws whatever it throws.</returns>
    /// <remarks>
    /// <b>Taking a permit again is not cancellable</b>, because it comes after the wait and must not
    /// replace what the wait threw - a printer that answered nothing is a verdict the pass acts on. It
    /// is a wait on other passes' work, which ends.
    /// </remarks>
    public async Task<T> WhilePrinterAnswersAsync<T>(Func<Task<T>> wait)
    {
        ArgumentNullException.ThrowIfNull(wait);

        Leave();

        try
        {
            return await wait();
        }
        finally
        {
            await budget.EnterAsync(CancellationToken.None);

            _holding = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Leave();
    }

    private void Leave()
    {
        if (_holding)
        {
            _holding = false;
            budget.Leave();
        }
    }
}
