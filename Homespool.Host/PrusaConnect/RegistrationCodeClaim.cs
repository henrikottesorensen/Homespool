using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore.Storage;

using Homespool.Host.Accounts;
using Homespool.Host.Exceptions;
using Homespool.Host.Services;
using Homespool.Model;
using Homespool.Model.Entities;

namespace Homespool.Host.PrusaConnect;

/// <summary>
/// A person redeeming a registration code they typed, bounded per account. Both places a code can be
/// typed - the claim page and <c>POST /api/v1/printers/register</c> - go through here, so the two
/// share one allowance rather than each handing a guesser a budget of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the only bound on guessing a code.</b> No rate limit reaches either caller, and a wrong
/// code finds no registration to count against, so the count lives on the account.
/// </para>
/// <para>
/// <b>Why this owns the transaction rather than living in
/// <see cref="PrusaConnectService.ClaimPrinterAsync"/>.</b> The service always runs inside its caller's
/// transaction, and a failure recorded there would enlist in it and be rolled back with the claim -
/// every wrong guess counting as zero. The attempt has to be counted before the transaction opens,
/// which only the code that opens it can arrange.
/// </para>
/// <para>
/// <b>Counted before the code is looked up, and given back when it was right.</b> Checking the
/// backoff, looking the code up and then counting let a burst of parallel claims all pass the check
/// before any was counted. <see cref="AttemptLimiter.TakeAttemptAsync"/> checks and counts in one
/// statement, so the claim that crosses the allowance backs off the rest of the burst.
/// </para>
/// </remarks>
public class RegistrationCodeClaim
{
    private readonly PrusaConnectService _prusaConnectService;
    private readonly UnitOfWork _unitOfWork;
    private readonly AttemptLimiter _attemptLimiter;
    private readonly TimeProvider _timeProvider;

    public RegistrationCodeClaim(PrusaConnectService prusaConnectService,
                                 UnitOfWork unitOfWork,
                                 AttemptLimiter attemptLimiter,
                                 TimeProvider timeProvider)
    {
        _prusaConnectService = prusaConnectService;
        _unitOfWork = unitOfWork;
        _attemptLimiter = attemptLimiter;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Claims the printer whose code <paramref name="typedCode"/> is, as <paramref name="userId"/>.
    /// </summary>
    /// <param name="userId">The account the attempt counts against.</param>
    /// <param name="typedCode">The code as the person entered it, before normalisation.</param>
    /// <param name="name">The printer's name, or <see langword="null"/> for none.</param>
    /// <param name="location">The printer's location, or <see langword="null"/> for none.</param>
    /// <param name="teamUuid">The team to claim into, or <see langword="null"/> for the caller's default.</param>
    /// <param name="caller">Who is claiming, for the team check.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <exception cref="ClaimLockedOutException">The account is backed off; the code was not looked at.</exception>
    /// <exception cref="PrinterNotFoundException">No live registration has this code. Counted.</exception>
    /// <exception cref="RegistrationAlreadyClaimedException">The code is right but already claimed. Not counted.</exception>
    /// <exception cref="TeamAccessDeniedException">The code is right but the team is not the caller's to claim into. Not counted.</exception>
    public async Task<Printer> ClaimAsync(long userId,
                                          string typedCode,
                                          string? name,
                                          string? location,
                                          Guid? teamUuid,
                                          Caller caller,
                                          CancellationToken cancellationToken)
    {
        // Codes are generated in Crockford base32 uppercase (CodeGenerator) and the TemporaryCode
        // lookup has no case-insensitive collation, so a code typed off a printer's screen with
        // different casing, stray whitespace or grouping hyphens would otherwise silently read as
        // unknown - and, being counted, cost an attempt for getting it right. Normalise also applies
        // Crockford's O/I/L substitutions, which is what makes a character misread off a
        // low-resolution LCD still resolve.
        string code = ClaimCode.Normalise(typedCode);

        // Before the code is compared, so a backed-off account cannot learn whether a guess was right
        // from which refusal comes back - and counted as a wrong guess until it proves otherwise.
        AttemptTicket attempt = await _attemptLimiter.TakeAttemptAsync(userId, LimitedAction.ClaimPrinter, _timeProvider.GetUtcNow(), cancellationToken);

        if (attempt.BackedOff is { } remaining)
        {
            throw new ClaimLockedOutException(remaining);
        }

        try
        {
            // Scoped INSIDE the try, so the transaction is disposed as an exception leaves this block
            // and the attempt is given back below on its own rather than inside a rollback.
            await using IDbContextTransaction transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

            Printer printer = await _prusaConnectService.ClaimPrinterAsync(code, name, location, teamUuid, caller);

            // Inside the transaction the claim was made in, so a rollback takes the reset with it.
            await _attemptLimiter.ResetAsync(userId, LimitedAction.ClaimPrinter, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return printer;
        }
        catch (Exception exception) when (exception is not PrinterNotFoundException)
        {
            // Not a guess. PrinterNotFoundException is the one outcome that is, and it stays counted;
            // an already-claimed code and a forbidden team both mean the code was *right*, so neither
            // counts - otherwise a user claiming into the wrong team would lock themselves out for
            // getting the code perfectly correct. Not cancellable: an abandoned request must not keep
            // an attempt its code did not earn.
            await _attemptLimiter.ReturnAttemptAsync(userId, LimitedAction.ClaimPrinter, attempt, CancellationToken.None);

            throw;
        }
    }
}
