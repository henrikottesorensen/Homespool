namespace Homespool.Host.Localisation;

/// <summary>
/// The marker type for Homespool's shared strings. Never instantiated — it exists so
/// <c>IStringLocalizer&lt;SharedResource&gt;</c> has something to name.
/// </summary>
/// <remarks>
/// <para>
/// <b>One shared resource rather than a file per view.</b> <c>IViewLocalizer</c>'s per-view
/// convention scatters a string across as many files as the pages that show it, and the same
/// sentence then gets translated twice and drifts. Homespool's pages share a small vocabulary —
/// statuses, actions, the words for a printer and a print — so one file is both smaller and more
/// consistent.
/// </para>
/// <para>
/// The <c>.resx</c> sits beside this class rather than under a <c>Resources</c> directory, so no
/// <c>ResourcesPath</c> is configured and the resource's manifest name is simply this type's full
/// name. One fewer piece of configuration to get subtly wrong.
/// </para>
/// </remarks>
public sealed class SharedResource
{
}
