using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Homespool.Analysers;

/// <summary>
/// HS0003: a <c>lock</c> statement holds a <c>System.Threading.Lock</c> and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The compiler reports the opposite mistake - a <c>Lock</c> assigned to an <c>object</c>, which
/// silently goes back to monitor-based locking (CS9216) - but nothing reports a lock target that was
/// never a <c>Lock</c> to begin with. The framework's own rule about locking on objects with weak
/// identity covers strings, types and the like, not a plain <c>object</c> field.
/// </para>
/// <para>
/// <b>Locking on the collection being guarded is the shape worth catching.</b> A dedicated
/// <c>object</c> field at least announces what it is for; <c>lock (_entries)</c> reads as deliberate,
/// takes the slower path, and exposes the monitor to anyone else holding that collection.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LockTargetAnalyser : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id.</summary>
    public const string DiagnosticId = "HS0003";

    /// <summary>The one type a lock statement may hold.</summary>
    private const string LockTypeName = "System.Threading.Lock";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Lock statement holds something other than System.Threading.Lock",
        "'{0}' is of type '{1}'; lock on a System.Threading.Lock instead",
        "Concurrency",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A lock statement holds a System.Threading.Lock, which the compiler turns into its own faster " +
                     "path and which cannot be locked on by anyone else holding the same value.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(compilation =>
        {
            INamedTypeSymbol? lockType = compilation.Compilation.GetTypeByMetadataName(LockTypeName);

            // Nothing to prefer on a target framework without the type, so the rule stands down
            // rather than reporting code that has no alternative.
            if (lockType is null)
            {
                return;
            }

            compilation.RegisterSyntaxNodeAction(node => AnalyseLock(node, lockType), SyntaxKind.LockStatement);
        });
    }

    private static void AnalyseLock(SyntaxNodeAnalysisContext context, INamedTypeSymbol lockType)
    {
        LockStatementSyntax statement = (LockStatementSyntax)context.Node;
        ITypeSymbol? target = context.SemanticModel.GetTypeInfo(statement.Expression, context.CancellationToken).Type;

        // An unresolved target is a compile error of its own; reporting it again would only add noise.
        if (target is null ||
            target.TypeKind == TypeKind.Error ||
            SymbolEqualityComparer.Default.Equals(target, lockType))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule,
                                                   statement.Expression.GetLocation(),
                                                   statement.Expression.ToString(),
                                                   target.ToDisplayString()));
    }
}
