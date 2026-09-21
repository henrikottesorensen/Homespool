using Homespool.Model.Entities;

namespace Homespool.Host.PrintFiles;

/// <summary>A file on disk, and the row describing it where there is one.</summary>
/// <param name="File">The bytes, as the store holds them - which is what makes it a file.</param>
/// <param name="Row">What is recorded about them, or null when nothing has indexed the file yet.</param>
public sealed record CataloguedFile(StoredFile File, PrintFile? Row);
