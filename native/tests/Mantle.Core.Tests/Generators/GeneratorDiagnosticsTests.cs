using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Mantle.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Mantle.Core.Tests.Generators;

// Mantle.Generators run in-process on small sources against Mantle.Core's own attributes: every malformed mark is a
// build ERROR with its MANTLE code and plain words (MANTLE001 bad name, 002 used twice, 003 wrong handler shape or
// When, 004 class cannot be made), and When = "X" emits a registration guarded by owner.X.
public class GeneratorDiagnosticsTests
{
    static (ImmutableArray<Diagnostic> Diags, string Catalog) Run(string source)
    {
        var tpa = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var refs = tpa.Where(p => !p.EndsWith("Mantle.Core.dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => MetadataReference.CreateFromFile(p)).Cast<MetadataReference>()
            .Append(MetadataReference.CreateFromFile(typeof(Mantle.Core.Commands.CommandAttribute).Assembly.Location));
        var comp = CSharpCompilation.Create("GenProbe", new[] { CSharpSyntaxTree.ParseText(source) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create(new CatalogGenerator()).RunGeneratorsAndUpdateCompilation(comp, out var output, out var diags);
        var catalog = driver.GetRunResult().GeneratedTrees.FirstOrDefault(t => t.FilePath.EndsWith("Mantle.CommandCatalog.g.cs", StringComparison.Ordinal))?.ToString() ?? "";
        // the generated code itself must compile (no error beyond what the generator reported)
        var compileErrors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && !d.Id.StartsWith("MANTLE", StringComparison.Ordinal)).ToArray();
        Assert.True(compileErrors.Length == 0, string.Join("\n", compileErrors.Select(d => d.ToString())));
        return (diags, catalog);
    }

    const string Usings = "using System.Threading.Tasks; using Mantle.Core.Commands; using Mantle.Core.Features;\n";

    static Diagnostic One(ImmutableArray<Diagnostic> d, string id)
    {
        var hit = d.Where(x => x.Id == id).ToArray();
        Assert.True(hit.Length == 1, $"expected one {id}, got: [{string.Join(" | ", d.Select(x => x.Id + " " + x.GetMessage()))}]");
        Assert.Equal(DiagnosticSeverity.Error, hit[0].Severity);
        return hit[0];
    }

    [Fact]
    public void MANTLE001_a_bad_command_name_is_an_error_in_words()
    {
        var (d, _) = Run(Usings + "public static class C { [Command(\"Bad Name\")] public static CommandResult A(CommandCall c) => CommandResult.Success(); }");
        Assert.Contains("is not a valid name: lower-case words joined by dots", One(d, "MANTLE001").GetMessage());
    }

    [Fact]
    public void MANTLE002_a_name_marked_twice_is_an_error_naming_both()
    {
        var (d, _) = Run(Usings + "public static class C { [Command(\"x.y\")] public static CommandResult A(CommandCall c) => CommandResult.Success();"
            + " [Command(\"x.y\")] public static CommandResult B(CommandCall c) => CommandResult.Success(); }");
        var m = One(d, "MANTLE002").GetMessage();
        Assert.Contains("C.A", m); Assert.Contains("C.B", m); Assert.Contains("every name must be unique", m);
    }

    [Fact]
    public void MANTLE003_a_wrong_handler_shape_or_a_bad_When_is_an_error_in_words()
    {
        Assert.Contains("a handler takes exactly one CommandCall",
            One(Run(Usings + "public static class C { [Command(\"x.y\")] public static CommandResult A(int c) => CommandResult.Success(); }").Diags, "MANTLE003").GetMessage());
        Assert.Contains("names no bool property or field on C",
            One(Run(Usings + "public sealed class C { [Command(\"x.y\", When = \"Nope\")] public CommandResult A(CommandCall c) => CommandResult.Success(); }").Diags, "MANTLE003").GetMessage());
        Assert.Contains("is a string, not a bool",
            One(Run(Usings + "public sealed class C { public string On => \"\"; [Command(\"x.y\", When = nameof(On))] public CommandResult A(CommandCall c) => CommandResult.Success(); }").Diags, "MANTLE003").GetMessage());
        Assert.Contains("is static but the handler is an instance method",
            One(Run(Usings + "public sealed class C { public static bool On => true; [Command(\"x.y\", When = nameof(On))] public CommandResult A(CommandCall c) => CommandResult.Success(); }").Diags, "MANTLE003").GetMessage());
        Assert.Contains("private or protected",
            One(Run(Usings + "public sealed class C { bool On => true; [Command(\"x.y\", When = nameof(On))] public CommandResult A(CommandCall c) => CommandResult.Success(); }").Diags, "MANTLE003").GetMessage());
    }

    [Fact]
    public void MANTLE004_a_feature_class_that_cannot_be_made_is_an_error_in_words()
    {
        var (d, _) = Run(Usings + "[Feature(\"notes\", \"Notes\", true)] public sealed class N { }");
        Assert.Contains("it does not implement Mantle.Core.Features.IFeature", One(d, "MANTLE004").GetMessage());
    }

    [Fact]
    public void When_registers_the_command_only_while_the_owner_says_so()
    {
        var (d, cat) = Run(Usings + "public sealed class C { public bool TestRun { get; set; }"
            + " [Command(\"x.fire\", When = nameof(TestRun))] public CommandResult Fire(CommandCall c) => CommandResult.Success();"
            + " [Command(\"x.list\")] public ValueTask<CommandResult> List(CommandCall c) => new(CommandResult.Success()); }");
        Assert.DoesNotContain(d, x => x.Severity == DiagnosticSeverity.Error);
        Assert.Contains("if (owner.TestRun) made.Add(bus.Register(new global::Mantle.Core.Commands.CommandRegistration(\"x.fire\"", cat);
        Assert.Matches(@"\n\s+made\.Add\(bus\.Register\(new global::Mantle\.Core\.Commands\.CommandRegistration\(""x\.list""", cat);
    }
}
