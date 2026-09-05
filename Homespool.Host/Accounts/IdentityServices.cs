// Identity's service registration, transcribed from dotnet/aspnetcore at v10.0.11
// (src/Identity/Core/src/IdentityServiceCollectionExtensions.cs, IdentityBuilderExtensions.cs and
// src/Identity/EntityFrameworkCore/src/IdentityEntityFrameworkBuilderExtensions.cs), with the generic
// parameters resolved to this application's types. Copyright (c) .NET Foundation, MIT licence.

using System;
using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Homespool.Data;
using Homespool.Host.Authentication;
using Homespool.Model.Entities;

namespace Homespool.Host.Accounts;

/// <summary>
/// What the framework's <c>AddIdentity</c>, <c>AddEntityFrameworkStores</c> and
/// <c>AddDefaultTokenProviders</c> register, written out for <see cref="HSUser"/> and
/// <see cref="IdentityRole{TKey}"/> so that every scoped service behind <see cref="UserManager{TUser}"/>
/// and <see cref="SignInManager{TUser}"/> can be read here rather than decompiled.
/// </summary>
/// <remarks>
/// <para>
/// <b>The authentication half lives in <c>Program</c>, not here.</b> The framework's <c>AddIdentity</c>
/// also sets the default authenticate, challenge and sign-in schemes and registers the four cookie
/// schemes; this application does both at the head of its own <c>AddAuthentication</c> chain, beside
/// the printer, token and OpenID Connect schemes, through <see cref="IdentityCookieSchemes"/>. Nothing
/// below registers a scheme, and <see cref="SignInManager{TUser}"/> will throw at the first sign-in
/// if that chain is missing - which is what the unit-test harness has to replicate.
/// </para>
/// <para>
/// <b>Same registrations, same order, same lifetimes, with one marked departure</b>:
/// <see cref="UsernameValidator"/> runs beside the framework's user validator, commented where it is
/// added. The one thing the framework does that this cannot is walk the
/// type hierarchy to pick a store: <c>AddEntityFrameworkStores</c> reflects over the context to find
/// the six framework entity types, where <see cref="AddHomespoolStores"/> simply names the ones
/// <see cref="HomespoolDbContext"/> inherits. A change to that base class therefore has to be
/// mirrored here by hand, and the compiler will say so - the store's constraints reject a mismatch.
/// </para>
/// <para>
/// <b>Everything is <c>TryAdd</c>, as upstream.</b> A registration made before this runs wins, which is
/// how the framework lets an application swap a validator or a claims factory. The one swap this
/// application makes - <see cref="HSIdentityErrorDescriber"/> through <c>AddErrorDescriber</c> - is
/// a plain <c>Add</c> made afterwards, and wins by being the last registration for its service.
/// </para>
/// </remarks>
public static class IdentityServices
{
    /// <summary>
    /// The scoped services <see cref="UserManager{TUser}"/>, <see cref="SignInManager{TUser}"/> and
    /// <see cref="RoleManager{TRole}"/> are built from: the service half of the framework's
    /// <c>AddIdentity</c>, resolved to <see cref="HSUser"/> and <see cref="IdentityRole{TKey}"/> over
    /// <see langword="long"/>.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">
    /// The <see cref="IdentityOptions"/> in force; <see cref="IdentityConfiguration.Configure"/> in
    /// the application and in any test that must apply the same rules.
    /// </param>
    /// <returns>An <see cref="IdentityBuilder"/> for the stores, describer and token providers.</returns>
    /// <remarks>
    /// <para>
    /// <b>The security stamp validator takes its clock from the container.</b> The framework falls
    /// back to the system clock when none is registered; the post-configure step below hands it
    /// whatever <see cref="TimeProvider"/> the container holds, which is what lets a test move time.
    /// </para>
    /// </remarks>
    public static IdentityBuilder AddHomespoolIdentity(this IServiceCollection services, Action<IdentityOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Hosting does not add IHttpContextAccessor by default, and SignInManager needs one.
        services.AddHttpContextAccessor();
        services.AddMetrics();

        services.TryAddScoped<IUserValidator<HSUser>, UserValidator<HSUser>>();
        services.TryAddScoped<IPasswordValidator<HSUser>, PasswordValidator<HSUser>>();
        services.TryAddScoped<IPasswordHasher<HSUser>, PasswordHasher<HSUser>>();

        // The framework's own normaliser, and deliberately so: it keys users AND roles, so anything
        // cleverer than upper-casing here changes what a role is called. A lookalike username is
        // refused by UsernameValidator at registration instead, which is the right layer for it.
        services.TryAddScoped<ILookupNormalizer, UpperInvariantLookupNormalizer>();
        services.TryAddScoped<IRoleValidator<IdentityRole<long>>, RoleValidator<IdentityRole<long>>>();

        // No interface: the framework adds errors to the describer without revving one, and so
        // can HSIdentityErrorDescriber, which AddErrorDescriber registers over this afterwards.
        services.TryAddScoped<IdentityErrorDescriber>();

        services.TryAddScoped<ISecurityStampValidator, SecurityStampValidator<HSUser>>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<SecurityStampValidatorOptions>, PostConfigureSecurityStampValidatorOptions>());
        services.TryAddScoped<ITwoFactorSecurityStampValidator, TwoFactorSecurityStampValidator<HSUser>>();
        services.TryAddScoped<IUserClaimsPrincipalFactory<HSUser>, UserClaimsPrincipalFactory<HSUser, IdentityRole<long>>>();
        services.TryAddScoped<IUserConfirmation<HSUser>, DefaultUserConfirmation<HSUser>>();
        services.TryAddScoped<IPasskeyHandler<HSUser>, PasskeyHandler<HSUser>>();
        services.TryAddScoped<UserManager<HSUser>>();
        services.TryAddScoped<SignInManager<HSUser>>();
        services.TryAddScoped<RoleManager<IdentityRole<long>>>();

        services.Configure(configure);

        // The one departure: a validator of this application's own, run after Identity's. It is
        // registered here rather than in Program because it decides what a username may BE, which
        // the test harness must agree with - the same reason IdentityConfiguration is shared.
        IdentityBuilder builder = new(typeof(HSUser), typeof(IdentityRole<long>), services);

        return builder.AddUserValidator<UsernameValidator>();
    }

    /// <summary>
    /// The Entity Framework stores over <see cref="HomespoolDbContext"/>: what
    /// <c>AddEntityFrameworkStores</c> arrives at by reflecting over the context's base class,
    /// spelled out.
    /// </summary>
    /// <remarks>
    /// <see cref="HomespoolDbContext"/> derives from <see cref="IdentityDbContext{TUser, TRole, TKey}"/>,
    /// which fills in the framework's own claim, user-role, login, role-claim, token and passkey
    /// entities over the key type; the store takes the same six, in its own parameter order. The passkey
    /// entity is part of the store's contract whether or not the context maps its table.
    /// </remarks>
    public static IdentityBuilder AddHomespoolStores(this IdentityBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<IUserStore<HSUser>,
            UserStore<HSUser, IdentityRole<long>, HomespoolDbContext, long,
                IdentityUserClaim<long>, IdentityUserRole<long>, IdentityUserLogin<long>,
                IdentityUserToken<long>, IdentityRoleClaim<long>, IdentityUserPasskey<long>>>();

        builder.Services.TryAddScoped<IRoleStore<IdentityRole<long>>,
            RoleStore<IdentityRole<long>, HomespoolDbContext, long, IdentityUserRole<long>, IdentityRoleClaim<long>>>();

        return builder;
    }

    /// <summary>
    /// The four token providers behind password reset, address and phone confirmation, and the
    /// authenticator app: the framework's <c>AddDefaultTokenProviders</c>, resolved to
    /// <see cref="HSUser"/>.
    /// </summary>
    /// <remarks>
    /// Each goes through <see cref="IdentityBuilder.AddTokenProvider(string, Type)"/> rather than a
    /// direct registration, because that method also records the provider under its name in
    /// <see cref="TokenOptions.ProviderMap"/>, and the descriptor it builds there is not constructible
    /// from outside the framework. The names are the ones <see cref="UserManager{TUser}"/> looks up
    /// by default, so a provider registered under any other name is never consulted.
    /// </remarks>
    public static IdentityBuilder AddHomespoolTokenProviders(this IdentityBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddTokenProvider(TokenOptions.DefaultProvider, typeof(DataProtectorTokenProvider<HSUser>))
                      .AddTokenProvider(TokenOptions.DefaultEmailProvider, typeof(EmailTokenProvider<HSUser>))
                      .AddTokenProvider(TokenOptions.DefaultPhoneProvider, typeof(PhoneNumberTokenProvider<HSUser>))
                      .AddTokenProvider(TokenOptions.DefaultAuthenticatorProvider, typeof(AuthenticatorTokenProvider<HSUser>));
    }

    /// <summary>
    /// Hands <see cref="SecurityStampValidatorOptions"/> the container's <see cref="TimeProvider"/>
    /// unless something set one already. The framework's own copy is private to its assembly.
    /// </summary>
    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
                     Justification = "Instantiated by the container, through the enumerable registration above.")]
    private sealed class PostConfigureSecurityStampValidatorOptions(TimeProvider? timeProvider = null)
        : IPostConfigureOptions<SecurityStampValidatorOptions>
    {
        // Left null rather than defaulted to TimeProvider.System: SecurityStampValidator already falls
        // back to the system clock itself.
        private readonly TimeProvider? _timeProvider = timeProvider;

        public void PostConfigure(string? name, SecurityStampValidatorOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            options.TimeProvider ??= _timeProvider;
        }
    }
}
