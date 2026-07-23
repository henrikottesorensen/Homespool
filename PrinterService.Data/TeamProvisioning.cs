using System;

using PrinterService.Model.Entities;

namespace PrinterService.Data;

/// <summary>
/// Creates the default team every user is entitled to at account creation.
/// </summary>
/// <remarks>
/// Both places that create a user need this — the bootstrap admin (<c>/setup</c>) and, later,
/// invite acceptance that mints a new account (phase-1.5 §15 step 6). It lives here, once, so the
/// second caller cannot drift from the first: the same reasoning that consolidated
/// <c>AccountConfirmationPolicy</c> in step 4 rather than letting a fourth copy appear.
/// </remarks>
public static class TeamProvisioning
{
    /// <summary>
    /// Stages a new default <see cref="Team"/> for <paramref name="userId"/>, with the user as its
    /// sole member holding full permissions. The team and its membership are <b>added but not
    /// saved</b> — the caller owns <c>SaveChanges</c>, so the team can share a transaction (or a
    /// compensating rollback) with the user creation that precedes it.
    /// </summary>
    /// <remarks>
    /// <see cref="Team.Name"/> is left null on purpose — see the property. The membership is the
    /// user's default; the filtered unique index in <c>PSDbContext</c> guarantees they never end up
    /// with two. Team key assignment flows to <see cref="TeamMember.TeamId"/> through the navigation
    /// on save, so no intermediate round-trip is needed.
    /// </remarks>
    public static Team AddDefaultTeam(this PSDbContext context, long userId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(context);

        Team team = new()
        {
            CreatedBy = userId,
            CreatedAt = createdAt,
            Members =
            {
                new TeamMember
                {
                    UserId = userId,
                    CanRead = true,
                    CanUse = true,
                    CanManage = true,
                    IsDefault = true,
                },
            },
        };

        context.Teams.Add(team);

        return team;
    }
}
