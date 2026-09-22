using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AwesomeAssertions;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Homespool.Analysers.Test;

public class LockTargetAnalyserTests
{
    /// <summary>
    /// The framework this test compiles against - its own, so <c>System.Threading.Lock</c> is the real
    /// type rather than a stand-in, which is the whole thing the rule asks about.
    /// </summary>
    private static readonly IReadOnlyList<MetadataReference> FrameworkReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToList();

    /// <summary>Runs HS0003 over a class holding <paramref name="members"/>, and returns what it reported.</summary>
    private static async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(string members)
    {
        string source = "using System.Collections.Generic;\nusing System.Threading;\n\nclass C\n{\n" + members + "\n}\n";

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, path: "Test.cs");
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            FrameworkReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ImmutableArray<Diagnostic> diagnostics = await compilation.WithAnalyzers([new LockTargetAnalyser()])
                                                                  .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        return diagnostics.Where(d => d.Id == LockTargetAnalyser.DiagnosticId).ToList();
    }

    private static async Task<IReadOnlyList<string>> ReportedTargetsAsync(string members)
    {
        IReadOnlyList<Diagnostic> diagnostics = await DiagnosticsAsync(members);

        return diagnostics.Select(d => d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan)).ToList();
    }

    [Fact]
    public async Task TheCompilationItselfIsValid()
    {
        // Arrange
        string members = "    private readonly Lock _gate = new();\n    void M() { lock (_gate) { } }";

        SyntaxTree tree = CSharpSyntaxTree.ParseText(
            "using System.Threading;\n\nclass C\n{\n" + members + "\n}\n",
            path: "Test.cs",
            cancellationToken: TestContext.Current.CancellationToken);

        CSharpCompilation compilation = CSharpCompilation.Create(
            "Test",
            [tree],
            FrameworkReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Act
        IEnumerable<Diagnostic> errors = compilation.GetDiagnostics(TestContext.Current.CancellationToken)
                                                    .Where(d => d.Severity == DiagnosticSeverity.Error);

        // Assert
        errors.Should().BeEmpty("a test that cannot resolve System.Threading.Lock would pass for the wrong reason");
    }

    [Theory]
    [InlineData("    private readonly object _gate = new();\n    void M() { lock (_gate) { } }", "_gate")]
    [InlineData("    private readonly List<int> _items = [];\n    void M() { lock (_items) { } }", "_items")]
    [InlineData("    private readonly Dictionary<int, int> _map = [];\n    void M() { lock (_map) { } }", "_map")]
    [InlineData("    public object Gate { get; } = new();\n    void M() { lock (Gate) { } }", "Gate")]
    [InlineData("    void M(object o) { lock (o) { } }", "o")]
    [InlineData("    static readonly object Shared = new();\n    void M() { lock (Shared) { } }", "Shared")]
    [InlineData("    private readonly object _gate = new();\n    void M() { lock (this._gate) { } }", "this._gate")]
    public async Task ReportsALockOnAnythingButALock(string members, string target)
    {
        IReadOnlyList<string> reported = await ReportedTargetsAsync(members);

        reported.Should().Equal(target);
    }

    [Fact]
    public async Task NamesTheTypeItFound()
    {
        IReadOnlyList<Diagnostic> reported = await DiagnosticsAsync(
            "    private readonly List<int> _items = [];\n    void M() { lock (_items) { } }");

        reported.Should().ContainSingle()
                .Which.GetMessage().Should().Contain("System.Collections.Generic.List<int>");
    }

    [Theory]
    [InlineData("    private readonly Lock _gate = new();\n    void M() { lock (_gate) { } }")]
    [InlineData("    private readonly Lock _gate = new();\n    void M() { lock (this._gate) { } }")]
    [InlineData("    void M(Lock gate) { lock (gate) { } }")]
    [InlineData("    private static readonly Lock Shared = new();\n    void M() { lock (Shared) { } }")]
    [InlineData("    private readonly Lock _gate = new();\n    Lock Gate => _gate;\n    void M() { lock (Gate) { } }")]
    public async Task LeavesALockAlone(string members)
    {
        IReadOnlyList<Diagnostic> reported = await DiagnosticsAsync(members);

        reported.Should().BeEmpty();
    }

    [Fact]
    public async Task ReportsEachLockStatementSeparately()
    {
        IReadOnlyList<string> reported = await ReportedTargetsAsync(
            "    private readonly object _first = new();\n" +
            "    private readonly object _second = new();\n" +
            "    void M() { lock (_first) { } lock (_second) { } }");

        reported.Should().Equal("_first", "_second");
    }

    /// <summary>
    /// A target that does not compile is somebody else's diagnostic, and reporting it again would put
    /// two errors on one mistake.
    /// </summary>
    [Fact]
    public async Task SaysNothingAboutATargetThatDoesNotResolve()
    {
        IReadOnlyList<Diagnostic> reported = await DiagnosticsAsync("    void M() { lock (_missing) { } }");

        reported.Should().BeEmpty();
    }
}
