using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

using Homespool.Data;
using Homespool.Host.Services;
using Homespool.Model.Entities;

namespace Homespool.Host.Test;

/// <summary>
/// Minting, resolving and revoking personal access tokens - the scheme whose whole safety property is
/// that finding the row by an indexed unsalted hash <i>is</i> verifying the credential
/// (<c>notes/api-tokens.md</c>).
/// </summary>
/// <remarks>
/// Run against real SQLite rather than the in-memory provider, like the sibling credential suites: the
/// uniqueness of the hash index and the cascade from the user are database behaviour, and asserting
/// them against a provider that fakes both would prove nothing.
/// </remarks>
public sealed class ApiTokenServiceTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"hs-apitoken-{Guid.NewGuid():N}.db");

    private static async Task<HSUser> AddUserAsync(HSDbContext context, string email = "owner@example.com")
    {
        HSUser user = new(email)
        {
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
        };

        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        return user;
    }

    private HSDbContext NewContext()
    {
        DbContextOptions<HSDbContext> options = new DbContextOptionsBuilder<HSDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        return new HSDbContext(options);
    }

    private async Task<HSDbContext> MigratedContextAsync()
    {
        HSDbContext context = NewContext();
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);

        return context;
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

    // ---------- the hash ----------

    /// <summary>
    /// <b>Pins the algorithm to SHA-384 with a known vector.</b> This hash is a lookup key, not a
    /// verified comparison: change the algorithm and every existing token stops resolving - silently,
    /// and looking exactly like revocation. A test that hashed with <c>SHA384.HashData</c> to build its
    /// own expectation would agree with any change; a literal disagrees.
    /// </summary>
    [Fact]
    public void HashSecretIsSha384Base64Url()
    {
        // Act
        string hash = ApiTokenService.HashSecret("abc");

        // Assert
        hash.Should().Be("ywB1P0WjXou1oD1pmsZQBycsMqsO3tFjGotgWkP_W-2AhgcroefMI1i67KE0yCWn");
    }

    // ---------- CreateAsync ----------

    /// <summary>
    /// The caller gets the prefixed plaintext once; the row keeps only its hash, and the hash is of the
    /// secret alone - not of the credential including its prefix.
    /// </summary>
    [Fact]
    public async Task CreateAsyncReturnsThePrefixedPlaintextAndStoresOnlyItsHash()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService service = new(context);

        // Act
        (ApiToken token, string plaintext) = await service.CreateAsync(user.Id, "laptop", CancellationToken.None);

        // Assert
        plaintext.Should().StartWith(ApiTokenService.Prefix);
        plaintext.Should().HaveLength(ApiTokenService.Prefix.Length + ApiTokenService.SecretLength);

        ApiToken stored = await context.ApiTokens.SingleAsync(TestContext.Current.CancellationToken);

        stored.Id.Should().Be(token.Id);
        stored.UserId.Should().Be(user.Id);
        stored.Name.Should().Be("laptop");
        stored.TokenHash.Should().NotContain(plaintext, "the plaintext must never be stored");
        stored.TokenHash.Should().Be(ApiTokenService.HashSecret(plaintext[ApiTokenService.Prefix.Length..]));
    }

    /// <summary>Two tokens are two secrets. Nothing about the owner or the name leaks into them.</summary>
    [Fact]
    public async Task CreateAsyncMintsADistinctSecretEachTime()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService service = new(context);

        // Act
        (_, string first) = await service.CreateAsync(user.Id, "laptop", CancellationToken.None);
        (_, string second) = await service.CreateAsync(user.Id, "laptop", CancellationToken.None);

        // Assert
        second.Should().NotBe(first);
    }

    // ---------- FindByCredentialAsync ----------

    /// <summary>A freshly minted credential resolves to its own row, which is the whole scheme.</summary>
    [Fact]
    public async Task FindByCredentialAsyncResolvesAFreshToken()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService service = new(context);

        (ApiToken created, string plaintext) = await service.CreateAsync(user.Id, "laptop", CancellationToken.None);

        // Act
        ApiToken? found = await service.FindByCredentialAsync(plaintext, CancellationToken.None);

        // Assert
        found.Should().NotBeNull();
        found!.Id.Should().Be(created.Id);
        found.UserId.Should().Be(user.Id);
    }

    /// <summary>
    /// The prefix is part of the credential, not decoration: the correct secret without it does not
    /// authenticate. This is what proves the prefix check happens rather than being cosmetic - a
    /// handler that stripped nothing and hashed whatever arrived would pass every other test here.
    /// </summary>
    [Fact]
    public async Task FindByCredentialAsyncRejectsTheRightSecretWithoutThePrefix()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService service = new(context);

        (_, string plaintext) = await service.CreateAsync(user.Id, "laptop", CancellationToken.None);
        string secretOnly = plaintext[ApiTokenService.Prefix.Length..];

        // Act
        ApiToken? found = await service.FindByCredentialAsync(secretOnly, CancellationToken.None);

        // Assert
        found.Should().BeNull();
    }

    /// <summary>An unrelated bearer credential of our own shape resolves to nothing.</summary>
    [Fact]
    public async Task FindByCredentialAsyncRejectsAnUnknownSecret()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService service = new(context);

        await service.CreateAsync(user.Id, "laptop", CancellationToken.None);

        // Act
        ApiToken? found = await service.FindByCredentialAsync(
            ApiTokenService.Prefix + new string('A', ApiTokenService.SecretLength), CancellationToken.None);

        // Assert
        found.Should().BeNull();
    }

    /// <summary>Nothing of the wrong shape reaches a query, and nothing of the wrong shape resolves.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hs_")]
    [InlineData("hs_tooshort")]
    [InlineData("Bearer hs_kJ3nR8xQvT2")]
    public async Task FindByCredentialAsyncRejectsMalformedCredentials(string? credential)
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService service = new(context);

        await service.CreateAsync(user.Id, "laptop", CancellationToken.None);

        // Act
        ApiToken? found = await service.FindByCredentialAsync(credential, CancellationToken.None);

        // Assert
        found.Should().BeNull();
    }

    /// <summary>Revocation is deletion, so a revoked credential stops resolving immediately.</summary>
    [Fact]
    public async Task FindByCredentialAsyncRejectsARevokedToken()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService service = new(context);

        (ApiToken created, string plaintext) = await service.CreateAsync(user.Id, "laptop", CancellationToken.None);

        // Act
        bool revoked = await service.RevokeAsync(user.Id, created.Id, CancellationToken.None);
        ApiToken? found = await service.FindByCredentialAsync(plaintext, CancellationToken.None);

        // Assert
        revoked.Should().BeTrue();
        found.Should().BeNull();
    }

    // ---------- RevokeAsync / ListAsync ----------

    /// <summary>
    /// Someone else's token id is not revocable, and the answer does not distinguish that from a token
    /// that never existed - so this cannot be used to probe for other people's ids.
    /// </summary>
    [Fact]
    public async Task RevokeAsyncWillNotDeleteAnotherUsersToken()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser owner = await AddUserAsync(context, "owner@example.com");
        HSUser other = await AddUserAsync(context, "other@example.com");
        ApiTokenService service = new(context);

        (ApiToken token, string plaintext) = await service.CreateAsync(owner.Id, "laptop", CancellationToken.None);

        // Act
        bool revoked = await service.RevokeAsync(other.Id, token.Id, CancellationToken.None);

        // Assert
        revoked.Should().BeFalse();
        (await service.FindByCredentialAsync(plaintext, CancellationToken.None)).Should().NotBeNull("the owner's token survives");
        (await service.RevokeAsync(other.Id, 9999, CancellationToken.None)).Should().BeFalse("an unknown id answers the same way");
    }

    /// <summary>A person sees their own tokens, newest first, and nobody else's.</summary>
    [Fact]
    public async Task ListAsyncReturnsOnlyTheOwnersTokensNewestFirst()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser owner = await AddUserAsync(context, "owner@example.com");
        HSUser other = await AddUserAsync(context, "other@example.com");
        ApiTokenService service = new(context);

        (ApiToken older, _) = await service.CreateAsync(owner.Id, "older", CancellationToken.None);
        (ApiToken newer, _) = await service.CreateAsync(owner.Id, "newer", CancellationToken.None);
        await service.CreateAsync(other.Id, "theirs", CancellationToken.None);

        // The two are minted in the same millisecond, so order by CreatedAt alone is not decidable -
        // separate them rather than assert on a tie the database is entitled to break either way.
        newer.CreatedAt = older.CreatedAt.AddMinutes(1);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<ApiToken> listed = await service.ListAsync(owner.Id, CancellationToken.None);

        // Assert
        listed.Should().HaveCount(2);
        listed[0].Name.Should().Be("newer");
        listed[1].Name.Should().Be("older");
    }

    /// <summary>
    /// Deleting an account takes its credentials with it, by cascade in the database rather than by
    /// application code remembering to. A live token pointing at a user id that no longer resolves
    /// would authenticate as nobody.
    /// </summary>
    [Fact]
    public async Task DeletingAUserRemovesTheirTokens()
    {
        // Arrange
        await using HSDbContext context = await MigratedContextAsync();
        HSUser user = await AddUserAsync(context);
        ApiTokenService service = new(context);

        (_, string plaintext) = await service.CreateAsync(user.Id, "laptop", CancellationToken.None);

        // Act
        context.Users.Remove(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Assert
        (await service.FindByCredentialAsync(plaintext, CancellationToken.None)).Should().BeNull();
    }
}
