using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

using Homespool.Analysers.CodeFixes;

namespace Homespool.Analysers.Test;

/// <summary>
/// Moving an operator: where it goes, where the operand it leaves behind ends up, and that nothing but
/// whitespace changes.
/// </summary>
public class OperatorPlacementTests
{
    private static string Rewrite(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        SourceText rewritten = OperatorPlacement.Apply(tree.GetRoot(), tree.GetText(), positions: null);
        SyntaxTree after = CSharpSyntaxTree.ParseText(rewritten);

        SyntaxFactory.AreEquivalent(tree.GetRoot(), after.GetRoot(), topLevel: false)
                     .Should().BeTrue("moving an operator changes whitespace and nothing else");

        return rewritten.ToString();
    }

    [Fact]
    public void AnOperatorAlignedUnderTheFirstOperandLeavesTheOperandThere()
    {
        const string before = """
                              if (a
                                  && b
                                  && c)
                              """;
        const string after = """
                             if (a &&
                                 b &&
                                 c)
                             """;

        Rewrite(before).Should().Be(after);
    }

    [Fact]
    public void AnOperatorHungInTheMarginLeavesTheOperandWhereItIs()
    {
        const string before = """
                              x = "one"
                                + "two"
                                + "three";
                              """;
        const string after = """
                             x = "one" +
                                 "two" +
                                 "three";
                             """;

        Rewrite(before).Should().Be(after);
    }

    [Fact]
    public void AConditionalIndentedUnderItsStatementKeepsTheIndent()
    {
        const string before = """
                              var x = a
                                  ? b
                                  : c;
                              """;
        const string after = """
                             var x = a ?
                                 b :
                                 c;
                             """;

        Rewrite(before).Should().Be(after);
    }

    [Fact]
    public void APatternCombinatorMoves()
    {
        const string before = """
                              bool r = s is A
                                         or B
                                         or C;
                              """;
        const string after = """
                             bool r = s is A or
                                           B or
                                           C;
                             """;

        Rewrite(before).Should().Be(after);
    }

    [Fact]
    public void TheOperatorGoesBeforeACommentEndingTheLine()
    {
        const string before = """
                              bool r = a // why a
                                       && b;
                              """;
        const string after = """
                             bool r = a && // why a
                                      b;
                             """;

        Rewrite(before).Should().Be(after);
    }

    [Fact]
    public void ACommentLineBetweenStaysWhereItIs()
    {
        const string before = """
                              bool r = a
                                       // why b
                                       && b;
                              """;
        const string after = """
                             bool r = a &&
                                      // why b
                                      b;
                             """;

        Rewrite(before).Should().Be(after);
    }

    [Fact]
    public void AnOperatorAloneOnItsLineTakesTheLineWithIt()
    {
        const string before = """
                              bool r = a
                                       &&
                                       b;
                              """;
        const string after = """
                             bool r = a &&
                                      b;
                             """;

        Rewrite(before).Should().Be(after);
    }

    [Fact]
    public void NestedOperatorsAllMove()
    {
        const string before = """
                              var x = (a
                                       || b)
                                  ? p
                                    + q
                                  : r;
                              """;
        const string after = """
                             var x = (a ||
                                      b) ?
                                 p +
                                 q :
                                 r;
                             """;

        Rewrite(before).Should().Be(after);
    }

    [Fact]
    public void AnOperatorBehindADirectiveIsLeftForAPerson()
    {
        const string before = """
                              bool r = a
                              #if true
                                       && b
                              #endif
                                       ;
                              """;

        Rewrite(before).Should().Be(before);
    }

    [Fact]
    public void AnOperatorFollowedByACommentIsLeftForAPerson()
    {
        const string before = """
                              bool r = a
                                       && /* why */ b;
                              """;

        Rewrite(before).Should().Be(before);
    }

    [Fact]
    public void OnlyTheRequestedOperatorsMove()
    {
        const string before = """
                              bool r = a
                                       && b;
                              bool s = c
                                       && d;
                              """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(before, cancellationToken: TestContext.Current.CancellationToken);
        int second = before.LastIndexOf("&&", System.StringComparison.Ordinal);

        string rewritten = OperatorPlacement.Apply(tree.GetRoot(TestContext.Current.CancellationToken),
                                                   tree.GetText(TestContext.Current.CancellationToken),
                                                   new HashSet<int> { second }).ToString();

        rewritten.Should().Be("""
                              bool r = a
                                       && b;
                              bool s = c &&
                                       d;
                              """);
    }

    /// <summary>
    /// The fix end to end, through the provider Rider calls: the diagnostic the analyser raises is one
    /// the fix accepts, and once applied the analyser has nothing left to say.
    /// </summary>
    [Fact]
    public async Task TheCodeFixClearsTheDiagnostic()
    {
        string source = OperatorPlacementAnalyserTests.Wrap("return a\n        && b\n        && a;");
        using AdhocWorkspace workspace = new();
        Document document = workspace.AddProject("Test", LanguageNames.CSharp).AddDocument("Test.cs", source);
        IReadOnlyList<Diagnostic> diagnostics = await OperatorPlacementAnalyserTests.DiagnosticsAsync(source);
        OperatorPlacementCodeFix fix = new();
        List<CodeAction> actions = [];

        Diagnostic first = diagnostics.OrderBy(d => d.Location.SourceSpan.Start).First();

        CodeFixContext context = new(document, first, (action, _) => actions.Add(action), TestContext.Current.CancellationToken);
        await fix.RegisterCodeFixesAsync(context);
        ImmutableArray<CodeActionOperation> operations = await actions.Should().ContainSingle().Subject
                                                                      .GetOperationsAsync(TestContext.Current.CancellationToken);
        Solution fixedSolution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
        SourceText fixedText = await fixedSolution.GetDocument(document.Id)!.GetTextAsync(TestContext.Current.CancellationToken);

        fixedText.ToString().Should().Contain("return a &&\n        b\n        && a;",
                                              "only the operator the diagnostic names moves");
        fix.FixableDiagnosticIds.Should().Equal(OperatorPlacementAnalyser.DiagnosticId);
        fix.GetFixAllProvider().Should().NotBeNull();
    }
}
