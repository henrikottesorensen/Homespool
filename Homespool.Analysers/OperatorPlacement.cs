using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Homespool.Analysers;

/// <summary>
/// Moves a leading operator to the end of the line before it.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the code fix so that it needs nothing beyond the compiler: the fix and a
/// whole-tree rewrite call the same code.
/// </para>
/// <para>
/// The operand left behind takes the operator's column, which keeps an operator that was aligned
/// under the first operand aligned. The exception is an operator hung in the margin so that the
/// operands line up - <c>return a</c> over <c>     + b</c> - where the operand is already where it
/// belongs and stays there.
/// </para>
/// </remarks>
public static class OperatorPlacement
{
    /// <summary>
    /// Moves every leading operator in <paramref name="root"/> whose position is in
    /// <paramref name="positions"/>, or every one when <paramref name="positions"/> is null.
    /// </summary>
    /// <remarks>
    /// An operator with a comment or directive where the edits would go is left for a person, and the
    /// analyser goes on reporting it.
    /// </remarks>
    /// <param name="root">The syntax tree's root.</param>
    /// <param name="text">The text <paramref name="root"/> was parsed from.</param>
    /// <param name="positions">Operator start positions to move, or null for all of them.</param>
    /// <returns>The rewritten text; <paramref name="text"/> itself when nothing could be moved.</returns>
    public static SourceText Apply(SyntaxNode root, SourceText text, ISet<int>? positions)
    {
        List<TextChange> changes = [];

        // How far each rewritten line's operand moved left. An operand lined up under one that has
        // itself moved has to move with it, and the first operand is always on an earlier line.
        Dictionary<int, (int operandStart, int shift)> shifts = [];

        foreach (LeadingOperator leading in LeadingOperator.FindAll(root, text)
                                                           .Where(l => positions is null || positions.Contains(l.Operator.SpanStart))
                                                           .OrderBy(l => l.Operator.SpanStart))
        {
            SyntaxToken op = leading.Operator;
            SyntaxToken previous = op.GetPreviousToken();
            SyntaxToken next = op.GetNextToken();
            TextLine line = text.Lines.GetLineFromPosition(op.SpanStart);

            if (op.LeadingTrivia.Any(IsDirective) ||
                op.TrailingTrivia.Any(t => !IsSpace(t)) ||
                previous.TrailingTrivia.Any(IsDirective) ||
                !string.IsNullOrWhiteSpace(text.ToString(TextSpan.FromBounds(line.Start, op.SpanStart))))
            {
                continue;
            }

            changes.Add(new TextChange(new TextSpan(previous.Span.End, 0), " " + op.Text));

            if (text.Lines.GetLineFromPosition(next.SpanStart).LineNumber != line.LineNumber)
            {
                changes.Add(new TextChange(line.SpanIncludingLineBreak, string.Empty));
                continue;
            }

            int opColumn = op.SpanStart - line.Start;
            int nextColumn = next.SpanStart - line.Start;
            int firstColumn = Column(text, leading.FirstOperand.SpanStart);
            int firstLine = text.Lines.GetLineFromPosition(leading.FirstOperand.SpanStart).LineNumber;
            int inherited = 0;

            if (shifts.TryGetValue(firstLine, out (int operandStart, int shift) moved) &&
                leading.FirstOperand.SpanStart >= moved.operandStart)
            {
                inherited = moved.shift;
            }

            int column = opColumn;

            if (nextColumn == firstColumn)
            {
                column = nextColumn - inherited;
            }
            else if (opColumn == firstColumn)
            {
                column = opColumn - inherited;
            }

            changes.Add(new TextChange(TextSpan.FromBounds(line.Start, next.SpanStart), new string(' ', column)));
            shifts[line.LineNumber] = (next.SpanStart, nextColumn - column);
        }

        return changes.Count == 0 ? text : text.WithChanges(changes.OrderBy(c => c.Span.Start));
    }

    private static int Column(SourceText text, int position)
    {
        return position - text.Lines.GetLineFromPosition(position).Start;
    }

    private static bool IsDirective(SyntaxTrivia trivia)
    {
        return trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia);
    }

    private static bool IsSpace(SyntaxTrivia trivia)
    {
        return trivia.IsKind(SyntaxKind.WhitespaceTrivia) || trivia.IsKind(SyntaxKind.EndOfLineTrivia);
    }
}
