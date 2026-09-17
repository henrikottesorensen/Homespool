using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace Homespool.Analysers.CodeFixes;

/// <summary>
/// Fixes HS0002 by moving the operator to the end of the line before it.
/// </summary>
[ExportCodeFixProvider(Microsoft.CodeAnalysis.LanguageNames.CSharp, Name = nameof(OperatorPlacementCodeFix))]
[Shared]
public sealed class OperatorPlacementCodeFix : CodeFixProvider
{
    private const string Title = "Move the operator to the end of the line before";

    /// <inheritdoc/>
    public override ImmutableArray<string> FixableDiagnosticIds => [OperatorPlacementAnalyser.DiagnosticId];

    /// <inheritdoc/>
    public override FixAllProvider GetFixAllProvider()
    {
        return FixAllProvider.Create(FixAllAsync);
    }

    /// <inheritdoc/>
    public override Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            context.RegisterCodeFix(
                CodeAction.Create(Title,
                                  cancellationToken => FixAsync(context.Document, [diagnostic], cancellationToken),
                                  nameof(OperatorPlacementCodeFix)),
                diagnostic);
        }

        return Task.CompletedTask;
    }

    private static async Task<Document?> FixAllAsync(FixAllContext context, Document document, ImmutableArray<Diagnostic> diagnostics)
    {
        return await FixAsync(document, diagnostics, context.CancellationToken).ConfigureAwait(false);
    }

    private static async Task<Document> FixAsync(Document document, IEnumerable<Diagnostic> diagnostics, CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);

        if (root is null)
        {
            return document;
        }

        HashSet<int> positions = [.. diagnostics.Select(d => d.Location.SourceSpan.Start)];

        return document.WithText(OperatorPlacement.Apply(root, text, positions));
    }
}
