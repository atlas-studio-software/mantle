using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mantle.Generators;

/// <summary>
/// Turns the three attributes into plain registration code (native/docs/contracts.md, "What the
/// generator will emit"):
///
///   [Command("name")]  on a method  -> Mantle.Generated.CommandCatalog: RegisterStatic(bus) for the static
///                                      ones, and one RegisterCommands(this ICommandBus, owner) per type with
///                                      instance ones, which that type's owner calls when it comes up;
///   [Feature(...)]     on a class   -> Mantle.Generated.FeatureCatalog.All (FeatureDescriptor list);
///   [Surface("name")]  on a class   -> Mantle.Generated.SurfaceCatalog.All (SurfaceDescriptor list).
///
/// Every catalog is emitted (empty when nothing is marked) wherever its attribute type is known, so a
/// caller never depends on a feature or surface existing yet. A malformed mark is a build ERROR in plain
/// words: a bad name, a name used twice, a handler with the wrong shape, a class that cannot be made.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class CatalogGenerator : IIncrementalGenerator
{
    public const string CommandAttr = "Mantle.Core.Commands.CommandAttribute";
    public const string FeatureAttr = "Mantle.Core.Features.FeatureAttribute";
    public const string SurfaceAttr = "Mantle.App.Shell.SurfaceAttribute";
    const string Bus = "global::Mantle.Core.Commands.ICommandBus";
    const string Reg = "global::Mantle.Core.Commands.CommandRegistration";
    const string Result = "global::Mantle.Core.Commands.CommandResult";

    // Names as Electron spells them: "qs.set", "bar.pin", "features.list", "open"
    static readonly Regex CommandName = new Regex("^[a-z][A-Za-z0-9]*(\\.[A-Za-z0-9]+)*$");
    // feature files: "clipboard", "_game-watch", "look-away"
    static readonly Regex FeatureName = new Regex("^[a-z_][a-z0-9_-]*$");
    // surfaces: "bar", "topbar", "launcher", "osd"
    static readonly Regex SurfaceName = new Regex("^[a-z][a-z0-9-]*$");

    static readonly DiagnosticDescriptor BadName = new("MANTLE001", "Bad name",
        "{0} \"{1}\" on {2} is not a valid name: {3}", "Mantle", DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor Duplicate = new("MANTLE002", "Name used twice",
        "{0} \"{1}\" is marked on both {2} and {3}; every name must be unique", "Mantle", DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor BadHandler = new("MANTLE003", "Wrong handler shape",
        "[Command(\"{0}\")] on {1}: {2}", "Mantle", DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor BadClass = new("MANTLE004", "Class cannot be made",
        "{0} on {1}: {2}", "Mantle", DiagnosticSeverity.Error, true);

    sealed class Found
    {
        public string Kind = "";          // command | feature | surface
        public string Name = "";
        public string TypeName = "";      // global::Ns.Type
        public string TypeShort = "";
        public string Method = "";
        public bool Static;
        public bool Sync;
        public string Init = "";          // feature: the attribute rebuilt as C#
        public string? Options;           // feature: the static Options member, when present
        public string Owner = "";         // command: the owner's words (a "const string Owner" on the type, else its name)
        public bool AtStart;              // surface
        public string? Error;             // plain words when the mark is malformed
        public DiagnosticDescriptor? ErrorKind;
        public Location? Where;
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var commands = context.SyntaxProvider.ForAttributeWithMetadataName(CommandAttr,
            static (n, _) => n is MethodDeclarationSyntax, static (c, _) => OfMethod(c)).Collect();
        var features = context.SyntaxProvider.ForAttributeWithMetadataName(FeatureAttr,
            static (n, _) => n is ClassDeclarationSyntax, static (c, _) => OfFeature(c)).Collect();
        var surfaces = context.SyntaxProvider.ForAttributeWithMetadataName(SurfaceAttr,
            static (n, _) => n is ClassDeclarationSyntax, static (c, _) => OfSurface(c)).Collect();
        var known = context.CompilationProvider.Select(static (c, _) => (
            Commands: c.GetTypeByMetadataName(CommandAttr) != null,
            Features: c.GetTypeByMetadataName(FeatureAttr) != null,
            Surfaces: c.GetTypeByMetadataName(SurfaceAttr) != null));
        var all = commands.Combine(features).Combine(surfaces).Combine(known);
        context.RegisterSourceOutput(all, static (spc, x) => {
            var cmd = x.Left.Left.Left; var feat = x.Left.Left.Right; var surf = x.Left.Right; var k = x.Right;
            if (k.Commands) spc.AddSource("Mantle.CommandCatalog.g.cs", Commands(spc, cmd));
            if (k.Features) spc.AddSource("Mantle.FeatureCatalog.g.cs", Features(spc, feat));
            if (k.Surfaces) spc.AddSource("Mantle.SurfaceCatalog.g.cs", Surfaces(spc, surf));
        });
    }

    // ------------------------------------------------------------------ reading the marks

    static string? StringArg(AttributeData a, int i)
        => a.ConstructorArguments.Length > i && a.ConstructorArguments[i].Value is string s ? s : null;

    static Found OfMethod(GeneratorAttributeSyntaxContext c)
    {
        var m = (IMethodSymbol)c.TargetSymbol;
        var t = m.ContainingType;
        var f = new Found {
            Kind = "command", Name = StringArg(c.Attributes[0], 0) ?? "", TypeName = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TypeShort = t.Name, Method = m.Name, Static = m.IsStatic, Where = c.TargetNode.GetLocation(),
        };
        bool hasOwner = t.GetMembers("Owner").OfType<IFieldSymbol>().Any(x => x.IsConst && x.Type.SpecialType == SpecialType.System_String && x.DeclaredAccessibility != Accessibility.Private);
        f.Owner = hasOwner ? f.TypeName + ".Owner" : Lit(t.Name);
        string? why = null;
        if (!CommandName.IsMatch(f.Name)) { f.ErrorKind = BadName; f.Error = "lower-case words joined by dots, like \"qs.set\" or \"bar.pin\""; return f; }
        string ret = m.ReturnType.ToDisplayString();
        if (ret == "System.Threading.Tasks.ValueTask<Mantle.Core.Commands.CommandResult>") f.Sync = false;
        else if (ret == "Mantle.Core.Commands.CommandResult") f.Sync = true;
        else why = $"it returns {ret}; a handler returns ValueTask<CommandResult> (or CommandResult)";
        if (why == null && (m.Parameters.Length != 1 || m.Parameters[0].Type.ToDisplayString() != "Mantle.Core.Commands.CommandCall"))
            why = "a handler takes exactly one CommandCall";
        if (why == null && m.IsGenericMethod) why = "a handler cannot be generic";
        if (why == null && (m.DeclaredAccessibility == Accessibility.Private || m.DeclaredAccessibility == Accessibility.Protected))
            why = "it is private or protected, so the generated registration cannot call it; make it internal or public";
        if (why == null && !Reachable(t)) why = "its class is private, generic or nested in one, so the generated registration cannot reach it";
        if (why != null) { f.ErrorKind = BadHandler; f.Error = why; }
        return f;
    }

    static bool Reachable(INamedTypeSymbol? t)
    {
        for (; t != null; t = t.ContainingType) {
            if (t.IsGenericType) return false;
            if (t.DeclaredAccessibility == Accessibility.Private || t.DeclaredAccessibility == Accessibility.Protected) return false;
        }
        return true;
    }

    static string? CannotMake(INamedTypeSymbol t, string iface)
    {
        if (t.IsAbstract || t.IsStatic) return "it is abstract or static, so it cannot be made";
        if (!Reachable(t)) return "it is private, generic or nested in one, so the generated catalog cannot reach it";
        if (!t.AllInterfaces.Any(i => i.ToDisplayString() == iface)) return "it does not implement " + iface;
        if (!t.InstanceConstructors.Any(k => k.Parameters.Length == 0 && k.DeclaredAccessibility != Accessibility.Private && k.DeclaredAccessibility != Accessibility.Protected))
            return "it has no parameterless constructor the catalog can call";
        return null;
    }

    static string Lit(string? s)
    {
        if (s == null) return "null";
        var b = new StringBuilder("\"");
        foreach (char ch in s) {
            switch (ch) {
                case '"': b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\n': b.Append("\\n"); break;
                case '\r': b.Append("\\r"); break;
                case '\t': b.Append("\\t"); break;
                default: if (ch < ' ') b.Append("\\u").Append(((int)ch).ToString("x4")); else b.Append(ch); break;
            }
        }
        return b.Append('"').ToString();
    }

    static string Strings(TypedConstant v)
        => v.Kind == TypedConstantKind.Array && !v.IsNull
            ? "new string[] { " + string.Join(", ", v.Values.Select(x => Lit(x.Value as string))) + " }"
            : "global::System.Array.Empty<string>()";

    static Found OfFeature(GeneratorAttributeSyntaxContext c)
    {
        var t = (INamedTypeSymbol)c.TargetSymbol;
        var a = c.Attributes[0];
        var f = new Found {
            Kind = "feature", Name = StringArg(a, 0) ?? "", TypeName = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TypeShort = t.Name, Where = c.TargetNode.GetLocation(),
        };
        if (!FeatureName.IsMatch(f.Name)) { f.ErrorKind = BadName; f.Error = "lower-case letters, digits and dashes, like \"clipboard\" (a leading _ marks a hub)"; return f; }
        string label = StringArg(a, 1) ?? "";
        bool on = a.ConstructorArguments.Length > 2 && a.ConstructorArguments[2].Value is bool b && b;
        var named = new List<string>();
        foreach (var kv in a.NamedArguments) {
            if (kv.Key == "Chords" || kv.Key == "Observe") named.Add(kv.Key + " = " + Strings(kv.Value));
            else if (kv.Key == "Rest" && kv.Value.Value is bool r) named.Add("Rest = " + (r ? "true" : "false"));
        }
        f.Init = $"new global::Mantle.Core.Features.FeatureAttribute({Lit(f.Name)}, {Lit(label)}, {(on ? "true" : "false")})"
               + (named.Count > 0 ? " { " + string.Join(", ", named) + " }" : "");
        var opt = t.GetMembers("Options").FirstOrDefault(m => m.IsStatic && (m is IPropertySymbol || m is IFieldSymbol));
        if (opt != null) f.Options = f.TypeName + ".Options";
        string? why = string.IsNullOrWhiteSpace(label) ? "it has no label (the words Settings shows)" : CannotMake(t, "Mantle.Core.Features.IFeature");
        if (why != null) { f.ErrorKind = BadClass; f.Error = why; }
        return f;
    }

    static Found OfSurface(GeneratorAttributeSyntaxContext c)
    {
        var t = (INamedTypeSymbol)c.TargetSymbol;
        var a = c.Attributes[0];
        var f = new Found {
            Kind = "surface", Name = StringArg(a, 0) ?? "", TypeName = t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            TypeShort = t.Name, Where = c.TargetNode.GetLocation(),
        };
        foreach (var kv in a.NamedArguments) if (kv.Key == "AtStart" && kv.Value.Value is bool s) f.AtStart = s;
        if (!SurfaceName.IsMatch(f.Name)) { f.ErrorKind = BadName; f.Error = "lower-case letters, digits and dashes, like \"bar\" or \"topbar\""; return f; }
        string? why = CannotMake(t, "Mantle.App.Shell.ISurface");
        if (why != null) { f.ErrorKind = BadClass; f.Error = why; }
        return f;
    }

    // ------------------------------------------------------------------ checking and writing

    /// Report every malformed mark and every duplicate; answer the good, unique ones in a stable order.
    static List<Found> Checked(SourceProductionContext spc, ImmutableArray<Found> all, string what)
    {
        var good = new List<Found>();
        var byName = new Dictionary<string, Found>(StringComparer.Ordinal);
        foreach (var f in all.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.TypeName, StringComparer.Ordinal).ThenBy(x => x.Method, StringComparer.Ordinal)) {
            string on = f.Method.Length > 0 ? f.TypeShort + "." + f.Method : f.TypeShort;
            if (f.Error != null) {
                if (f.ErrorKind == BadName) spc.ReportDiagnostic(Diagnostic.Create(BadName, f.Where, what, f.Name, on, f.Error));
                else if (f.ErrorKind == BadHandler) spc.ReportDiagnostic(Diagnostic.Create(BadHandler, f.Where, f.Name, on, f.Error));
                else spc.ReportDiagnostic(Diagnostic.Create(BadClass, f.Where, what, on, f.Error));
                continue;
            }
            if (byName.TryGetValue(f.Name, out var first)) {
                string firstOn = first.Method.Length > 0 ? first.TypeShort + "." + first.Method : first.TypeShort;
                spc.ReportDiagnostic(Diagnostic.Create(Duplicate, f.Where, what, f.Name, firstOn, on));
                continue;
            }
            byName[f.Name] = f;
            good.Add(f);
        }
        return good;
    }

    const string Header = "// <auto-generated>Mantle.Generators.CatalogGenerator; edit the marked classes, not this file.</auto-generated>\n#nullable enable\n";

    static string Handler(Found f, string target)
        => f.Sync
            ? $"c => new global::System.Threading.Tasks.ValueTask<{Result}>({target}.{f.Method}(c))"
            : $"c => {target}.{f.Method}(c)";

    static string Commands(SourceProductionContext spc, ImmutableArray<Found> found)
    {
        var good = Checked(spc, found, "[Command]");
        var s = new StringBuilder(Header);
        s.Append("namespace Mantle.Generated\n{\n");
        s.Append("    /// <summary>Every [Command] of this assembly, registered without reflection.</summary>\n");
        s.Append("    internal static class CommandCatalog\n    {\n");
        s.Append("        /// <summary>Every command of this assembly: its name, its owner's type, and whether it is static.</summary>\n");
        s.Append("        public static global::System.Collections.Generic.IReadOnlyList<(string Name, string Owner, bool Static)> All { get; } = new (string, string, bool)[] {\n");
        foreach (var f in good) s.Append($"            ({Lit(f.Name)}, {Lit(f.TypeShort)}, {(f.Static ? "true" : "false")}),\n");
        s.Append("        };\n\n");
        s.Append("        /// <summary>Register every static [Command]. Disposing the answer unregisters them; a refused one (a\n");
        s.Append("        /// duplicate) unregisters those already made and throws the bus's own words.</summary>\n");
        s.Append($"        public static global::System.IDisposable RegisterStatic({Bus} bus)\n        {{\n");
        s.Append("            var made = new Batch();\n            try {\n");
        foreach (var f in good.Where(x => x.Static))
            s.Append($"                made.Add(bus.Register(new {Reg}({Lit(f.Name)}, {f.Owner}, {Handler(f, f.TypeName)})));\n");
        s.Append("            } catch { made.Dispose(); throw; }\n            return made;\n        }\n");
        foreach (var g in good.Where(x => !x.Static).GroupBy(x => x.TypeName).OrderBy(x => x.Key, StringComparer.Ordinal)) {
            string shortName = g.First().TypeShort;
            s.Append($"\n        /// <summary>Register {shortName}'s instance commands on <paramref name=\"owner\"/>.</summary>\n");
            s.Append($"        public static global::System.IDisposable RegisterCommands(this {Bus} bus, {g.Key} owner)\n        {{\n");
            s.Append("            if (owner == null) throw new global::System.ArgumentNullException(nameof(owner));\n");
            s.Append("            var made = new Batch();\n            try {\n");
            foreach (var f in g) s.Append($"                made.Add(bus.Register(new {Reg}({Lit(f.Name)}, {f.Owner}, {Handler(f, "owner")})));\n");
            s.Append("            } catch { made.Dispose(); throw; }\n            return made;\n        }\n");
        }
        s.Append("\n        sealed class Batch : global::System.IDisposable\n        {\n");
        s.Append("            readonly global::System.Collections.Generic.List<global::System.IDisposable> _all = new();\n");
        s.Append("            public void Add(global::System.IDisposable d) => _all.Add(d);\n");
        s.Append("            public void Dispose() { for (int i = _all.Count - 1; i >= 0; i--) _all[i].Dispose(); _all.Clear(); }\n");
        s.Append("        }\n    }\n}\n");
        return s.ToString();
    }

    static string Features(SourceProductionContext spc, ImmutableArray<Found> found)
    {
        var good = Checked(spc, found, "[Feature]");
        var s = new StringBuilder(Header);
        s.Append("namespace Mantle.Generated\n{\n");
        s.Append("    /// <summary>One FeatureDescriptor per [Feature] class of this assembly; Create makes a fresh instance.</summary>\n");
        s.Append("    internal static class FeatureCatalog\n    {\n");
        s.Append("        public static global::System.Collections.Generic.IReadOnlyList<global::Mantle.Core.Features.FeatureDescriptor> All { get; } = new global::Mantle.Core.Features.FeatureDescriptor[] {\n");
        foreach (var f in good)
            s.Append($"            global::Mantle.Core.Features.FeatureDescriptor.Of({f.Init}, static () => new {f.TypeName}(){(f.Options != null ? ", " + f.Options : "")}),\n");
        s.Append("        };\n    }\n}\n");
        return s.ToString();
    }

    static string Surfaces(SourceProductionContext spc, ImmutableArray<Found> found)
    {
        var good = Checked(spc, found, "[Surface]");
        var s = new StringBuilder(Header);
        s.Append("namespace Mantle.Generated\n{\n");
        s.Append("    /// <summary>One SurfaceDescriptor per [Surface] class of this assembly.</summary>\n");
        s.Append("    internal static class SurfaceCatalog\n    {\n");
        s.Append("        public static global::System.Collections.Generic.IReadOnlyList<global::Mantle.App.Shell.SurfaceDescriptor> All { get; } = new global::Mantle.App.Shell.SurfaceDescriptor[] {\n");
        foreach (var f in good)
            s.Append($"            new global::Mantle.App.Shell.SurfaceDescriptor({Lit(f.Name)}, {(f.AtStart ? "true" : "false")}, static () => new {f.TypeName}()),\n");
        s.Append("        };\n    }\n}\n");
        return s.ToString();
    }
}
