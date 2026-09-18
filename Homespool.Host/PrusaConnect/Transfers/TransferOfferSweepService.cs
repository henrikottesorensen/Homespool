using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Hosting;

namespace Homespool.Host.PrusaConnect.Transfers;

/// <summary>
/// Closes abandoned transfer offers on a timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing else reaches the last one.</b> <see cref="TransferOfferStore"/> sweeps whenever a new
/// offer is made, which bounds how many pile up and says nothing about when: the offer made just
/// before a quiet night keeps its file handle, its key and its fetchable URL until somebody sends
/// another file. A printer finishing a transfer releases its own offer, so what is left for this is
/// the printer that acknowledged the command and never came back.
/// </para>
/// <para>
/// The interval is short against <see cref="TransferOfferStore.CollectWithin"/> because it is how
/// far past that limit a handle and a key can linger. It is not how far past it an offer can be
/// <i>opened</i> - <see cref="TransferOfferStore.TryOpen"/> checks the limit itself - and a pass
/// is a scan of a handful of dictionary entries.
/// </para>
/// </remarks>
public sealed class TransferOfferSweepService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly TransferOfferStore _offers;
    private readonly TimeProvider _timeProvider;

    public TransferOfferSweepService(TransferOfferStore offers, TimeProvider timeProvider)
    {
        _offers = offers;
        _timeProvider = timeProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(SweepInterval, _timeProvider);

        try
        {
            // No pass before the first tick: the store is empty when the host starts, since an
            // offer does not survive a restart.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _offers.SweepIdle();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown - stoppingToken fired while awaiting the timer.
        }
    }
}
