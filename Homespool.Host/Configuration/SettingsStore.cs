using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;

using Homespool.Host.Localisation;

namespace Homespool.Host.Configuration;

/// <summary>
/// Reads and writes the settings an administrator may change, and is the only thing that writes the
/// settings file while the application is running.
/// </summary>
/// <remarks>
/// <para>
/// <b>Validation happens before the write, not at the next start.</b> A live setting takes effect the
/// moment the file is reloaded, so a value that fails its range would break a running deployment
/// rather than a starting one — and the page is the last place that can say so while somebody is
/// still looking at the field they typed it into.
/// </para>
/// <para>
/// <b>A secret is never read back out to a browser.</b> The page renders
/// <see cref="SecretPlaceholder"/> when one is stored, and a post carrying that placeholder means
/// "leave it alone". That lets somebody change the sender name without re-typing a password they
/// were never shown, and still lets a typed password replace the stored one — the case that must
/// keep working or a password could never be changed at all.
/// </para>
/// <para>
/// <b>The placeholder does not carry a secret to a different destination.</b> A secret is only
/// worth what it unlocks, and the settings marked <see cref="EditableSetting.BindsSecret"/> decide
/// what that is: the server, how it is reached, and the account. When any of them differs from what
/// is in force, the placeholder is refused and the secret must be typed again. Otherwise an
/// administrator who was never told the password could send it to a server of their own - and TLS
/// is no defence there, because whoever names the server can hold a valid certificate for it. Both
/// <see cref="Save"/> and <see cref="CandidateFor"/> enforce it: a save is what the next restart
/// and every mail after it use, and a candidate is what the test button connects to now.
/// </para>
/// </remarks>
public sealed class SettingsStore
{
    /// <summary>
    /// What the page shows in place of a stored secret, and what it means when posted back.
    /// </summary>
    public const string SecretPlaceholder = "****";

    private readonly IConfiguration _configuration;
    private readonly SettingsFile _file;
    private readonly SettingsSecretProtector _protector;
    private readonly IStringLocalizer<SharedResource> _localiser;

    /// <summary>Creates the store.</summary>
    /// <param name="configuration">The application's configuration, reloaded after a write.</param>
    /// <param name="file">The file the values are stored in.</param>
    /// <param name="protector">Encrypts and decrypts the secrets among them.</param>
    /// <param name="localiser">The refusal a secret that cannot be carried over is reported with.</param>
    public SettingsStore(IConfiguration configuration,
                         SettingsFile file,
                         SettingsSecretProtector protector,
                         IStringLocalizer<SharedResource> localiser)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(localiser);

        _configuration = configuration;
        _file = file;
        _protector = protector;
        _localiser = localiser;
    }

    /// <summary>
    /// The value each editable setting currently has, as the application sees it.
    /// </summary>
    /// <remarks>
    /// A secret answers <see cref="SecretPlaceholder"/> when one is stored and empty when none is, so
    /// a caller cannot render one by accident.
    /// </remarks>
    /// <returns>Every editable path and its current value.</returns>
    public IReadOnlyDictionary<string, string> Current()
    {
        Dictionary<string, string> values = [];

        Dictionary<Type, object> bound = [];

        foreach (EditableSetting setting in EditableSettings.All)
        {
            if (setting.IsSecret)
            {
                values[setting.Path] = HasStoredSecret(setting) ? SecretPlaceholder : string.Empty;

                continue;
            }

            // Bound, not read raw. Most of these have no entry in appsettings.json at all - their
            // value is the property initialiser - so asking configuration directly answers null and
            // the page shows an empty box for a setting that is very much in force. Binding gives
            // the value the application is actually using, which is the only one worth showing.
            if (!bound.TryGetValue(setting.OptionsType, out object? instance))
            {
                instance = Candidate(setting.OptionsType, setting.Section, new Dictionary<string, string?>());
                bound[setting.OptionsType] = instance;
            }

            object? value = setting.OptionsType.GetProperty(setting.Key)?.GetValue(instance);

            values[setting.Path] = value switch
            {
                null => string.Empty,
                bool flag => flag ? "true" : "false",
                IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
        }

        return values;
    }

    /// <summary>
    /// Applies a set of submitted values, writing nothing unless all of them are valid.
    /// </summary>
    /// <param name="submitted">Values keyed by <see cref="EditableSetting.Path"/>.</param>
    /// <returns>What happened, and any errors keyed by path.</returns>
    public SettingsSaveResult Save(IReadOnlyDictionary<string, string?> submitted)
    {
        ArgumentNullException.ThrowIfNull(submitted);

        JsonObject stored = _file.Read();
        Dictionary<string, string?> pending = [];

        foreach (EditableSetting setting in EditableSettings.All)
        {
            if (!submitted.TryGetValue(setting.Path, out string? value))
            {
                continue;
            }

            if (setting.IsSecret)
            {
                // The placeholder means the browser was never told the secret and is handing back
                // what it was shown. Anything else is a real answer, including an empty one, which
                // clears it.
                if (value == SecretPlaceholder)
                {
                    continue;
                }

                pending[setting.StoredPath] = _protector.Protect(value);

                continue;
            }

            pending[setting.Path] = value;
        }

        Dictionary<string, string> errors = new(Validate(pending), StringComparer.Ordinal);

        foreach (string path in SecretsThatCannotCarryOver(submitted))
        {
            errors[path] = _localiser["Settings_Refuse_SecretNotCarriedOver"];
        }

        if (errors.Count > 0)
        {
            return new SettingsSaveResult(false, errors);
        }

        foreach ((string path, string? value) in pending)
        {
            Write(stored, path, value);
        }

        _file.Write(stored);

        // The layer is registered without a watcher, so this is what makes the write visible. It
        // also fires the change token every IOptionsMonitor is listening on.
        (_configuration as IConfigurationRoot)?.Reload();

        return new SettingsSaveResult(true, errors);
    }

    /// <summary>
    /// What a section would look like if these values were applied, without applying them.
    /// </summary>
    /// <remarks>
    /// <b>Answers "would this work?" for the mail test.</b> Mail settings only take effect at the next
    /// restart, so testing the running configuration would answer a question nobody asked - what the
    /// deployment is doing, rather than what was just typed. It refuses exactly where
    /// <see cref="Save"/> would - a value that will not convert or fails its checks, and a secret that
    /// cannot carry over - so a test never runs on values a save would refuse, nor reaches a server a
    /// save could not. A refused secret is not revealed at all.
    /// </remarks>
    /// <typeparam name="T">The options type.</typeparam>
    /// <param name="submitted">Values keyed by <see cref="EditableSetting.Path"/>.</param>
    /// <returns>
    /// An instance carrying the current values with the submitted ones over them, or the reasons
    /// there is none.
    /// </returns>
    public SettingsCandidate<T> CandidateFor<T>(IReadOnlyDictionary<string, string?> submitted)
        where T : class, new()
    {
        ArgumentNullException.ThrowIfNull(submitted);

        List<EditableSetting> settings = [.. EditableSettings.All.Where(setting => setting.OptionsType == typeof(T))];

        if (settings.Count == 0)
        {
            return new SettingsCandidate<T>(new T(), new Dictionary<string, string>());
        }

        string section = settings[0].Section;

        HashSet<string> refused = [.. SecretsThatCannotCarryOver(submitted)];

        Dictionary<string, string?> overlay = [];

        foreach (EditableSetting setting in settings)
        {
            if (!submitted.TryGetValue(setting.Path, out string? value) || refused.Contains(setting.Path))
            {
                continue;
            }

            // A secret comes back from a browser as the mask when it was never shown, which means
            // "the stored one" - so the stored one is what gets tested, the check above having
            // established it is being tested against what it was stored for.
            overlay[setting.Key] = setting.IsSecret && value == SecretPlaceholder ?
                _protector.Reveal(_configuration[setting.StoredPath], setting.StoredPath) :
                value;
        }

        object candidate = Checked(typeof(T), section, overlay, out Dictionary<string, string> errors);

        foreach (EditableSetting setting in settings.Where(setting => refused.Contains(setting.Path)))
        {
            errors[setting.Path] = _localiser["Settings_Refuse_SecretNotCarriedOver"];
        }

        return errors.Count > 0 ?
            new SettingsCandidate<T>(null, errors) :
            new SettingsCandidate<T>((T)candidate, errors);
    }

    /// <summary>
    /// The secrets submitted as the placeholder whose section's binding settings are being changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Compared as bound values, against what is in force</b> - the settings file over everything
    /// beneath it, so a password supplied by the environment is held to the same rule as a stored
    /// one. Binding is what makes <c>587</c> and <c>0587</c>, or <c>True</c> and <c>true</c>, the
    /// same answer. Text is compared without regard to case: a host name has none, and an account name
    /// differing only in case still goes to the same server. A value refused on its own - one that
    /// will not convert, or fails its checks - names no server the secret could reach, so it changes
    /// nothing here and is reported against its own field alone.
    /// </para>
    /// <para>
    /// <b>No further normalisation, deliberately.</b> Two spellings that name the same server - a
    /// trailing dot, a Unicode name and its ASCII form - are treated as different, which costs only a
    /// re-typed password. Treating two different servers as the same would cost the password.
    /// </para>
    /// <para>
    /// A placeholder is refused here whether or not a secret is stored. With nothing stored there is
    /// nothing to carry, and the answer that works is still to type one.
    /// </para>
    /// <para>
    /// <b>A secret left out of the submission counts as the placeholder</b>, since it has the same
    /// effect: <see cref="Save"/> leaves the stored one where it is while writing the new server
    /// beside it. A browser posts every field on the form, so this is the shape a request has when
    /// it was not built by the page.
    /// </para>
    /// </remarks>
    private List<string> SecretsThatCannotCarryOver(IReadOnlyDictionary<string, string?> submitted)
    {
        List<string> refused = [];

        foreach (IGrouping<Type, EditableSetting> group in EditableSettings.All.GroupBy(setting => setting.OptionsType))
        {
            List<EditableSetting> secrets = [.. group.Where(setting => setting.IsSecret)];

            if (secrets.Count == 0)
            {
                continue;
            }

            List<EditableSetting> binding = [.. group.Where(setting => setting.BindsSecret && submitted.ContainsKey(setting.Path))];

            if (binding.Count == 0)
            {
                continue;
            }

            string section = group.First().Section;

            object current = Candidate(group.Key, section, new Dictionary<string, string?>());
            object proposed = Checked(
                group.Key,
                section,
                binding.ToDictionary(setting => setting.Key, setting => submitted[setting.Path]),
                out Dictionary<string, string> invalid);

            if (binding.All(setting => invalid.ContainsKey(setting.Path) ||
                                       SameDestination(group.Key, setting.Key, current, proposed)))
            {
                continue;
            }

            refused.AddRange(secrets.Where(setting => CarriesOver(setting, submitted, current))
                                    .Select(setting => setting.Path));
        }

        return refused;
    }

    /// <summary>
    /// Whether this secret would keep its stored value rather than being given a new one.
    /// </summary>
    /// <remarks>
    /// The placeholder says so outright. A path the submission never mentions says the same thing by
    /// omission, but only when there is a value to keep - a section with no secret in force has
    /// nothing to carry anywhere, and refusing there would block a save that changes a host before
    /// mail is configured at all.
    /// </remarks>
    private bool CarriesOver(EditableSetting setting, IReadOnlyDictionary<string, string?> submitted, object current)
    {
        if (submitted.TryGetValue(setting.Path, out string? value))
        {
            return value == SecretPlaceholder;
        }

        return !string.IsNullOrEmpty(_configuration[setting.StoredPath]) ||
               !string.IsNullOrEmpty(setting.OptionsType.GetProperty(setting.Key)!.GetValue(current) as string);
    }

    private static bool SameDestination(Type type, string key, object current, object proposed)
    {
        PropertyInfo property = type.GetProperty(key)!;

        object? before = property.GetValue(current);
        object? after = property.GetValue(proposed);

        return before is string text ?
            string.Equals(text, after as string, StringComparison.OrdinalIgnoreCase) :
            Equals(before, after);
    }

    private object Candidate(Type type, string section, IReadOnlyDictionary<string, string?> overlay)
    {
        return Candidate(type, section, overlay, out _);
    }

    /// <summary>
    /// The section as it is in force, with the submitted values bound over it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The binder decides what a value can be</b>, because it is what reads the settings file at
    /// the next load: anything accepted here is accepted there. It throws on the first value it cannot
    /// convert - <c>abc</c> for a number, <c>70000</c> for a <c>ushort</c>, <c>0,5</c> for a
    /// <c>double</c> - so each value is bound on its own. One that will not convert is reported in
    /// <paramref name="unreadable"/> and leaves its property at the value in force, which keeps it out
    /// of every other check: it is not also out of range, and it is not a different mail server.
    /// </para>
    /// <para>
    /// Each binding names one scalar property, so a conversion is the only thing it can fail on.
    /// </para>
    /// </remarks>
    private object Candidate(Type type,
                             string section,
                             IReadOnlyDictionary<string, string?> overlay,
                             out List<string> unreadable)
    {
        object instance = Activator.CreateInstance(type)!;

        _configuration.GetSection(section).Bind(instance);

        unreadable = [];

        foreach ((string key, string? value) in overlay)
        {
            try
            {
                new ConfigurationBuilder().AddInMemoryCollection([new(key, value)]).Build().Bind(instance);
            }
            catch (InvalidOperationException)
            {
                unreadable.Add(key);
            }
        }

        return instance;
    }

    /// <summary>
    /// Binds the submitted values over a section and checks the result, reporting each failure
    /// against the path it belongs to.
    /// </summary>
    /// <param name="type">The options type.</param>
    /// <param name="section">Its configuration section.</param>
    /// <param name="overlay">Submitted values, keyed by property name.</param>
    /// <param name="errors">Every failure, a value that would not convert or one that fails its checks.</param>
    /// <returns>The candidate instance.</returns>
    private object Checked(Type type,
                           string section,
                           IReadOnlyDictionary<string, string?> overlay,
                           out Dictionary<string, string> errors)
    {
        object instance = Candidate(type, section, overlay, out List<string> unreadable);

        errors = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string key in unreadable)
        {
            errors[PathOf(section, key)] = Unreadable(type.GetProperty(key)!);
        }

        List<ValidationResult> results = [];

        Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true);

        foreach (ValidationResult result in results)
        {
            foreach (string member in result.MemberNames)
            {
                errors.TryAdd(PathOf(section, member), result.ErrorMessage ?? "Invalid.");
            }
        }

        return instance;
    }

    /// <summary>
    /// What a value the binder would not convert is reported as: what the field takes.
    /// </summary>
    /// <remarks>
    /// The bounds are the property's <see cref="RangeAttribute"/> where it has one and the type's
    /// own otherwise, and are written in the invariant culture because that is what the binder
    /// reads - somebody who typed <c>0,5</c> is shown <c>0.1</c>, not another comma.
    /// </remarks>
    private string Unreadable(PropertyInfo property)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        string? key = Type.GetTypeCode(type) switch
        {
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or
                TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 => "Settings_Refuse_NotAWholeNumber",
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => "Settings_Refuse_NotANumber",
            _ => null,
        };

        if (key is null)
        {
            return _localiser["Settings_Refuse_UnreadableValue"];
        }

        RangeAttribute? range = property.GetCustomAttribute<RangeAttribute>();

        object minimum = range?.Minimum ?? type.GetField("MinValue")!.GetValue(null)!;
        object maximum = range?.Maximum ?? type.GetField("MaxValue")!.GetValue(null)!;

        return string.Format(
            CultureInfo.CurrentCulture,
            _localiser[key],
            Convert.ToString(minimum, CultureInfo.InvariantCulture),
            Convert.ToString(maximum, CultureInfo.InvariantCulture));
    }

    private static string PathOf(string section, string key)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{section}:{key}");
    }

    private static void Write(JsonObject stored, string path, string? value)
    {
        string[] parts = path.Split(':');

        if (stored[parts[0]] is not JsonObject section)
        {
            section = [];
            stored[parts[0]] = section;
        }

        if (value is null)
        {
            section.Remove(parts[1]);

            return;
        }

        section[parts[1]] = JsonValue.Create(value);
    }

    private bool HasStoredSecret(EditableSetting setting)
    {
        return !string.IsNullOrEmpty(_configuration[setting.StoredPath]) ||
               !string.IsNullOrEmpty(_configuration[setting.Path]);
    }

    /// <summary>
    /// Binds what the deployment would have after this save and checks it, so a bad value is refused
    /// while somebody can still see the field it came from.
    /// </summary>
    private IReadOnlyDictionary<string, string> Validate(IReadOnlyDictionary<string, string?> pending)
    {
        Dictionary<string, string> errors = [];

        foreach (IGrouping<Type, EditableSetting> group in EditableSettings.All.GroupBy(setting => setting.OptionsType))
        {
            string section = group.First().Section;

            // A secret is ciphertext by the time it reaches here and would fail any check on the
            // property it decrypts into, so it takes no part in this.
            Dictionary<string, string?> overlay = group
                .Where(setting => !setting.IsSecret && pending.ContainsKey(setting.Path))
                .ToDictionary(setting => setting.Key, setting => pending[setting.Path]);

            if (overlay.Count == 0)
            {
                continue;
            }

            Checked(group.Key, section, overlay, out Dictionary<string, string> found);

            foreach (EditableSetting setting in group.Where(setting => overlay.ContainsKey(setting.Key)))
            {
                if (found.TryGetValue(setting.Path, out string? error))
                {
                    errors[setting.Path] = error;
                }
            }
        }

        return errors;
    }
}

/// <summary>What a save did.</summary>
/// <param name="Saved">Whether the file was written.</param>
/// <param name="Errors">Any validation failures, keyed by <see cref="EditableSetting.Path"/>.</param>
public sealed record SettingsSaveResult(bool Saved, IReadOnlyDictionary<string, string> Errors);

/// <summary>What a set of submitted values would make of an options section.</summary>
/// <typeparam name="T">The options type.</typeparam>
/// <param name="Value">The resulting options, or null when <paramref name="Errors"/> is not empty.</param>
/// <param name="Errors">Why there is no <paramref name="Value"/>, keyed by <see cref="EditableSetting.Path"/>.</param>
public sealed record SettingsCandidate<T>(T? Value, IReadOnlyDictionary<string, string> Errors)
    where T : class;
