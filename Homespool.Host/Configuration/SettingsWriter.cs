using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;

namespace Homespool.Host.Configuration;

/// <summary>
/// Writes a settings file from the editable values this deployment currently carries in its
/// environment, so that moving a setting out of <c>compose.yaml</c> does not lose what an operator
/// had set.
/// </summary>
/// <remarks>
/// <para>
/// <b>A one-shot, not a startup step, and the difference is what makes it work.</b> An upgrade
/// replaces the image and <c>compose.yaml</c> together, and compose passes only the variables that
/// file names - so by the time new code runs for the first time, the variables this exists to rescue
/// are already unmapped. Anything that seeded on startup would find nothing and write defaults. The
/// order that does work is: take the new image, run this once while the <i>old</i> compose is still
/// in place, then switch compose.
/// </para>
/// <para>
/// <b>Only what is explicitly set, never a default.</b> Writing the current default of every
/// editable setting would freeze it: a later improvement to a default would silently never reach a
/// deployment whose file already names it. A key absent from this file means "whatever the code
/// says", which is the property worth keeping.
/// </para>
/// <para>
/// <b>Environment variables only.</b> That is exactly what an operator's <c>.env</c> reaches the
/// container as. <c>appsettings.json</c> ships inside the image and is not an operator's to set, so
/// copying it into a writable file would freeze shipped values for no reason.
/// </para>
/// <para>
/// <b>The SMTP password is written in the clear, and that is temporary.</b> Nothing protects it yet;
/// when the protector arrives, a file carrying a plaintext password is used as it stands and
/// converted on its next save - the same adopt-on-save rule a camera credential already follows. The
/// file is written 0600 either way.
/// </para>
/// </remarks>
public static class SettingsWriter
{
    /// <summary>The entry-point argument that asks for this. Not a server run.</summary>
    /// <remarks>
    /// An <i>older</i> image does not recognise it, hands it to the host builder and starts the
    /// server - the trap the other applets here document. Anything scripting this must bound it in
    /// time and check that a file appeared, rather than trusting the exit code.
    /// </remarks>
    public const string Argument = "--write-settings";

    /// <summary>
    /// Writes the editable settings found in the environment to <paramref name="path"/>.
    /// </summary>
    /// <param name="path">
    /// Where to write. Optional: the configured settings path is used when this is null or empty.
    /// </param>
    /// <returns>Zero when a file was written or there was nothing to write, one on failure.</returns>
    public static int Write(string? path)
    {
        return WriteFrom(new ConfigurationBuilder().AddEnvironmentVariables().Build(), path);
    }

    /// <summary>
    /// Writes the editable settings found in <paramref name="configuration"/> to <paramref name="path"/>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Write(string?)"/> so the source can be something other than the
    /// process environment. Setting environment variables from a test is process-global, and this
    /// suite runs in parallel.
    /// </remarks>
    /// <param name="configuration">Where the values are read from.</param>
    /// <param name="path">
    /// Where to write. Optional: the configured settings path is used when this is null or empty.
    /// </param>
    /// <returns>Zero when a file was written or there was nothing to write, one on failure.</returns>
    public static int WriteFrom(IConfiguration configuration, string? path)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string target = string.IsNullOrWhiteSpace(path) ?
            SettingsFile.Resolve(configuration[SettingsFile.PathConfigurationKey], Directory.GetCurrentDirectory()) :
            path;

        SettingsFile file = new(target);

        // Refusing rather than merging, following the schema writer: the operator can see what is
        // there and delete it, and a silent merge into a file somebody has already edited by hand is
        // the kind of help nobody asked for.
        if (file.Exists)
        {
            Console.Error.WriteLine($"{Argument}: '{target}' already exists. Refusing to overwrite a " +
                                    "settings file - delete it first if that is what you meant.");

            return 1;
        }

        JsonObject contents = [];
        int written = 0;

        foreach (EditableSetting setting in EditableSettings.All)
        {
            string? value = configuration[setting.Path];

            if (value is null)
            {
                continue;
            }

            if (contents[setting.Section] is not JsonObject section)
            {
                section = [];
                contents[setting.Section] = section;
            }

            section[setting.Key] = Typed(setting, value);
            written++;

            // The value is deliberately absent from this line: one of these is a password.
            Console.WriteLine($"{setting.Path}");
        }

        if (written == 0)
        {
            Console.WriteLine($"{Argument}: nothing set in the environment; no file written.");

            return 0;
        }

        try
        {
            file.Write(contents);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"{Argument}: could not write '{target}': {ex.Message}");

            return 1;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"{Argument}: could not write '{target}': {ex.Message}");

            return 1;
        }

        Console.WriteLine($"{Argument}: wrote {written} setting(s) to {target}.");

        return 0;
    }

    /// <summary>
    /// Converts a configuration string to the JSON shape the property's type expects.
    /// </summary>
    /// <remarks>
    /// Configuration binding would accept a string for all of these, but the file is meant to be read
    /// and hand-edited: <c>"Port": 587</c> says what it is and <c>"Port": "587"</c> invites somebody
    /// to wonder. An unparseable value is written as the string it was rather than dropped, so a
    /// mistake stays visible instead of vanishing during a migration.
    /// </remarks>
    private static JsonNode Typed(EditableSetting setting, string value)
    {
        PropertyInfo? property = setting.OptionsType.GetProperty(
            setting.Key,
            BindingFlags.Public | BindingFlags.Instance);

        Type type = property?.PropertyType ?? typeof(string);

        if (type == typeof(bool) && bool.TryParse(value, out bool flag))
        {
            return JsonValue.Create(flag);
        }

        if ((type == typeof(int) || type == typeof(long) || type == typeof(ushort))
            && long.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out long whole))
        {
            return JsonValue.Create(whole);
        }

        if (type == typeof(double)
            && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double real))
        {
            return JsonValue.Create(real);
        }

        return JsonValue.Create(value);
    }
}
