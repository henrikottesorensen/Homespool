using System.Collections.Generic;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Homespool.Analysers;

/// <summary>
/// An operator that begins a wrapped line instead of ending the line before it.
/// </summary>
public readonly struct LeadingOperator
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LeadingOperator"/> struct.
    /// </summary>
    /// <param name="op">The operator token.</param>
    /// <param name="firstOperand">The first token of the leftmost operand the operator joins.</param>
    public LeadingOperator(SyntaxToken op, SyntaxToken firstOperand)
    {
        Operator = op;
        FirstOperand = firstOperand;
    }

    /// <summary>Gets the operator token.</summary>
    public SyntaxToken Operator { get; }

    /// <summary>
    /// Gets the first token of the leftmost operand the operator joins - where a hand-aligned
    /// expression lines its operands up.
    /// </summary>
    public SyntaxToken FirstOperand { get; }

    /// <summary>
    /// Every operator in <paramref name="root"/> that is the first token on its line.
    /// </summary>
    /// <remarks>
    /// Binary operators, the pattern combinators <c>and</c> and <c>or</c>, <c>is</c>, and both halves
    /// of a conditional. Not the <c>:</c> of a constructor initializer or a base list, which SA1128
    /// puts on its own line, nor the <c>.</c> of a call chain or <c>=&gt;</c>, which are not
    /// operators joining two operands.
    /// </remarks>
    /// <param name="root">The syntax tree's root.</param>
    /// <param name="text">The text <paramref name="root"/> was parsed from.</param>
    /// <returns>The leading operators, in document order within each expression.</returns>
    public static IEnumerable<LeadingOperator> FindAll(SyntaxNode root, SourceText text)
    {
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            switch (node)
            {
                case BinaryExpressionSyntax binary when LeadsItsLine(binary.OperatorToken, text):
                    yield return new LeadingOperator(binary.OperatorToken, binary.Left.GetFirstToken());
                    break;

                case BinaryPatternSyntax pattern when LeadsItsLine(pattern.OperatorToken, text):
                    yield return new LeadingOperator(pattern.OperatorToken, pattern.Left.GetFirstToken());
                    break;

                case IsPatternExpressionSyntax isPattern when LeadsItsLine(isPattern.IsKeyword, text):
                    yield return new LeadingOperator(isPattern.IsKeyword, isPattern.Expression.GetFirstToken());
                    break;

                case ConditionalExpressionSyntax conditional:
                    SyntaxToken condition = conditional.Condition.GetFirstToken();

                    if (LeadsItsLine(conditional.QuestionToken, text))
                    {
                        yield return new LeadingOperator(conditional.QuestionToken, condition);
                    }

                    if (LeadsItsLine(conditional.ColonToken, text))
                    {
                        yield return new LeadingOperator(conditional.ColonToken, condition);
                    }

                    break;
            }
        }
    }

    private static bool LeadsItsLine(SyntaxToken token, SourceText text)
    {
        SyntaxToken previous = token.GetPreviousToken();

        if (previous.RawKind == 0 || token.IsMissing)
        {
            return false;
        }

        int previousLine = text.Lines.GetLineFromPosition(previous.Span.End).LineNumber;

        return previousLine < text.Lines.GetLineFromPosition(token.SpanStart).LineNumber;
    }
}
