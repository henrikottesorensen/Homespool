namespace Homespool.Host.Printing;

/// <summary>
/// Something a caller wants a printer to do, in Homespool's own vocabulary - protocol-free by
/// construction. Each protocol translates an intent into its own wire command at the edge (for
/// Prusa Connect that is <c>PrusaIntentTranslator</c>), and a protocol that cannot express one
/// refuses it there rather than pretending. The vocabulary is drawn against more than one
/// protocol; <c>notes/domain-vocabulary.md</c> carries the mapping tables.
/// </summary>
public interface IPrinterIntent
{
    /// <summary>
    /// The intent's own name, for logs and failure bodies - the type name, never a protocol's wire
    /// word: <c>StopPrint</c>, where the Prusa wire says <c>STOP_PRINT</c>.
    /// </summary>
    string Name => GetType().Name;
}
