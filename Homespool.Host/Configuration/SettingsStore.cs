using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;

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
/// "leave it alone". That is keyed on the placeholder rather than on the form being otherwise
/// unchanged, which is the distinction that matters: it lets somebody correct the mail host without
/// re-typing a password they were never shown, and still lets a typed password replace the stored
/// one — the case that must keep working or a password could never be changed at all.
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

    /// <summary>Creates the store.</summary>
    /// <param name="configuration">The application's configuration, reloaded after a write.</param>
    /// <param name="file">The file the values are stored in.</param>
    /// <param name="protector">Encrypts and decrypts the secrets among them.</param>
    public SettingsStore(IConfiguration configuration, SettingsFile file, SettingsSecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(protector);

        _configuration = configuration;
        _file = file;
        _protector = protector;
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

        foreach (EditableSetting setting in EditableSettings.All)
        {
            values[setting.Path] = setting.IsSecret ?
                (HasStoredSecret(setting) ? SecretPlaceholder : string.Empty) :
                _configuration[setting.Path] ?? string.Empty;
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

        IReadOnlyDictionary<string, string> errors = Validate(pending);

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
        return !string.IsNullOrEmpty(_configuration[setting.StoredPath])
               || !string.IsNullOrEmpty(_configuration[setting.Path]);
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

            object instance = Activator.CreateInstance(group.Key)!;

            _configuration.GetSection(section).Bind(instance);

            new ConfigurationBuilder().AddInMemoryCollection(overlay).Build().Bind(instance);

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
