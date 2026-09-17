using System.Collections.Immutable;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Homespool.Analysers;

/// <summary>
/// HS0002: an operator that wraps a line ends the line it belongs to rather than leading the next.
/// </summary>
/// <remarks>
/// <c>.editorconfig</c> states the rule for Rider, and <c>dotnet_style_operator_placement_when_wrapping</c>
/// states it for Roslyn's formatter, but neither reports code that already breaks it - IDE0055 steers a
/// rewrap and does not audit what is there. This does.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class OperatorPlacementAnalyser : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id.</summary>
    public const string DiagnosticId = "HS0002";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        "Operator leads a wrapped line",
        "'{0}' leads its line; it belongs at the end of the line before",
        "Layout",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "When an expression wraps, the operator stays on the line it follows and the next line starts with the operand.");

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxTreeAction(AnalyseTree);
    }

    private static void AnalyseTree(SyntaxTreeAnalysisContext context)
    {
        SyntaxNode root = context.Tree.GetRoot(context.CancellationToken);
        SourceText text = context.Tree.GetText(context.CancellationToken);

        foreach (LeadingOperator leading in LeadingOperator.FindAll(root, text))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, leading.Operator.GetLocation(), leading.Operator.Text));
        }
    }
}
