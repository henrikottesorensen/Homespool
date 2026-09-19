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
    /// <see cref="Save"/> would, so the test cannot reach a server a save could not.
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

        Dictionary<string, string> errors = SecretsThatCannotCarryOver(submitted)
            .Where(path => settings.Any(setting => setting.Path == path))
            .ToDictionary(path => path, path => (string)_localiser["Settings_Refuse_SecretNotCarriedOver"], StringComparer.Ordinal);

        if (errors.Count > 0)
        {
            return new SettingsCandidate<T>(null, errors);
        }

        if (settings.Count == 0)
        {
            return new SettingsCandidate<T>(new T(), errors);
        }

        string section = settings[0].Section;

        Dictionary<string, string?> overlay = [];

        foreach (EditableSetting setting in settings)
        {
            if (!submitted.TryGetValue(setting.Path, out string? value))
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

        return new SettingsCandidate<T>((T)Candidate(typeof(T), section, overlay), errors);
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
    /// differing only in case still goes to the same server.
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
    /// </remarks>
    private List<string> SecretsThatCannotCarryOver(IReadOnlyDictionary<string, string?> submitted)
    {
        List<string> refused = [];

        foreach (IGrouping<Type, EditableSetting> group in EditableSettings.All.GroupBy(setting => setting.OptionsType))
        {
            List<string> masked =
            [
                .. group.Where(setting => setting.IsSecret &&
                                          submitted.TryGetValue(setting.Path, out string? value) &&
                                          value == SecretPlaceholder)
                        .Select(setting => setting.Path),
            ];

            if (masked.Count == 0)
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
            object proposed = Candidate(
                group.Key,
                section,
                binding.ToDictionary(setting => setting.Key, setting => submitted[setting.Path]));

            if (binding.Any(setting => !SameDestination(group.Key, setting.Key, current, proposed)))
            {
                refused.AddRange(masked);
            }
        }

        return refused;
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
        object instance = Activator.CreateInstance(type)!;

        _configuration.GetSection(section).Bind(instance);

        new ConfigurationBuilder().AddInMemoryCollection(overlay).Build().Bind(instance);

        return instance;
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

            object instance = Candidate(group.Key, section, overlay);

            List<ValidationResult> results = [];

            if (Validator.TryValidateObject(instance, new ValidationContext(instance), results, validateAllProperties: true))
            {
                continue;
            }

            foreach (ValidationResult result in results)
            {
                foreach (string member in result.MemberNames)
                {
                    string path = string.Create(CultureInfo.InvariantCulture, $"{section}:{member}");

                    if (overlay.ContainsKey(member))
                    {
                        errors[path] = result.ErrorMessage ?? "Invalid.";
                    }
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
