using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.CSharp;

public sealed class RoslynSlicerTests
{
    private const LanguageVersion V = LanguageVersion.CSharp12;
    private static readonly LanguageTarget Lang = new(V, "C# 12");
    private static readonly QwenTokenCounter Tokens = QwenTokenCounter.Shared;
    private const int Huge = 1_000_000;

    private static string LoadModern() =>
        File.ReadAllText(CorpusLocation.Get("Modern/Shop.Api/Services/OrderService.cs")).Replace("\r\n", "\n");

    private static (string Source, MemberCandidate Target) Cancel()
    {
        var src = LoadModern();
        return (src, Assert.Single(MemberLocator.FindByName(src, V, null, "CancelAsync")));
    }

    private static SlicePart Part(SliceResult r, string kind) => r.Slice.Included.Single(p => p.Kind == kind);

    [Fact]
    public void Target_IsVerbatimSourceOfMember()
    {
        var (src, target) = Cancel();

        var r = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []);

        var expected = string.Join('\n', src.Split('\n')[(target.Span.StartLine - 1)..target.Span.EndLine]);
        Assert.Equal(expected, Part(r, "target").Text);
        Assert.StartsWith("        /// <summary>", Part(r, "target").Text);
        Assert.Equal("target", r.Regions[0].Reason);
        Assert.Equal(target.Span, r.Regions[0].Span);
        Assert.False(r.TargetTooLarge);
        Assert.Equal("C# 12", r.Slice.LanguageHint);
    }

    [Fact]
    public void Signatures_ReplaceBodiesWithEllipsis()
    {
        var (src, target) = Cancel();

        var text = Part(RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []), "signatures").Text;

        Assert.Contains("public async Task<decimal> CalculateRefundAsync(int orderId) { ... }", text.Split('\n'));
        Assert.Contains("public async Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request) { ... }", text.Split('\n'));
        Assert.DoesNotContain("환불액", text);
        Assert.DoesNotContain("CancelAsync", text);
        Assert.DoesNotContain("OrderService(", text);
    }

    [Fact]
    public void Fields_IncludeFieldsAndConstructorSignature()
    {
        var (src, target) = Cancel();

        var text = Part(RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []), "fields").Text;

        Assert.Contains("private readonly IOrderRepository _repository;", text);
        Assert.Contains("OrderService(IOrderRepository repository, ILogger<OrderService> logger) { ... }", text);
        Assert.DoesNotContain("ArgumentNullException", text);
    }

    [Fact]
    public void Usings_PartAndRegion_MatchFileUsingLines()
    {
        var (src, target) = Cancel();
        var lines = src.Split('\n');

        var r = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []);

        Assert.Equal(string.Join('\n', lines[0..7]), Part(r, "usings").Text);
        var region = r.Regions.Single(x => x.Reason == "usings");
        Assert.Equal(new TextSpanInfo(1, 7), region.Span);
    }

    [Fact]
    public void NoLineNumbersInAnyPart()
    {
        var (src, target) = Cancel();

        var r = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []);

        var numbered = new Regex(@"^\s*\d+\s*[:|.)]\s");
        foreach (var part in r.Slice.Included)
            foreach (var line in part.Text.Split('\n'))
                Assert.DoesNotMatch(numbered, line);
    }

    [Fact]
    public void Slice_DtoRemovedAfterSignatures_BeforeUsings()
    {
        var (src, target) = Cancel();
        const string dto = "class Order: int OrderId; OrderStatus Status";
        var full = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, [], dto);
        int T(string k) => Part(full, k).Tokens;
        var total = full.Slice.Included.Sum(p => p.Tokens);

        var one = RoslynSlicer.Build(src, target, Lang, Tokens, total - 1, [], dto);
        var two = RoslynSlicer.Build(src, target, Lang, Tokens, total - T("signatures") - 1, [], dto);
        var three = RoslynSlicer.Build(src, target, Lang, Tokens, total - T("signatures") - T("dto") - 1, [], dto);

        Assert.Equal(dto, Part(full, "dto").Text);
        Assert.Equal(2, Part(full, "dto").Priority);
        Assert.Equal(["signatures"], one.Slice.Omitted.Select(p => p.Kind));
        Assert.Equal(["signatures", "dto"], two.Slice.Omitted.Select(p => p.Kind));
        Assert.Equal(["signatures", "dto", "usings"], three.Slice.Omitted.Select(p => p.Kind));
        Assert.DoesNotContain(RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []).Slice.Included, p => p.Kind == "dto");
    }

    [Fact]
    public void OverBudget_DropsSignaturesThenUsingsThenFields()
    {
        var (src, target) = Cancel();
        var full = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []);
        int T(string k) => Part(full, k).Tokens;
        var total = full.Slice.Included.Sum(p => p.Tokens);

        var one = RoslynSlicer.Build(src, target, Lang, Tokens, total - 1, []);
        var two = RoslynSlicer.Build(src, target, Lang, Tokens, total - T("signatures") - 1, []);
        var three = RoslynSlicer.Build(src, target, Lang, Tokens, T("target") + T("fields") - 1, []);

        Assert.Equal(["signatures"], one.Slice.Omitted.Select(p => p.Kind));
        Assert.Equal(["signatures", "usings"], two.Slice.Omitted.Select(p => p.Kind));
        Assert.Equal(["signatures", "usings", "fields"], three.Slice.Omitted.Select(p => p.Kind));
        Assert.Equal(["target"], three.Slice.Included.Select(p => p.Kind));
        Assert.False(three.TargetTooLarge);
        Assert.True(one.Slice.Included.Sum(p => p.Tokens) <= total - 1);
    }

    [Fact]
    public void UsingsDropped_RemovesUsingsRegion()
    {
        var (src, target) = Cancel();
        var full = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []);
        var total = full.Slice.Included.Sum(p => p.Tokens);
        var budget = total - Part(full, "signatures").Tokens - 1;

        var r = RoslynSlicer.Build(src, target, Lang, Tokens, budget, []);

        Assert.DoesNotContain(r.Slice.Included, p => p.Kind == "usings");
        Assert.Equal(["target"], r.Regions.Select(x => x.Reason));
    }

    [Fact]
    public void TargetAloneOverBudget_SetsTargetTooLarge()
    {
        var src = File.ReadAllText(CorpusLocation.Get("Legacy/Shop.Batch/SettlementBatch.cs"), Encoding.Latin1).Replace("\r\n", "\n");
        var target = Assert.Single(MemberLocator.FindByName(src, V, null, "BuildReport"));

        var r = RoslynSlicer.Build(src, target, Lang, Tokens, 500, []);

        Assert.True(r.TargetTooLarge);
        Assert.Equal(["target"], r.Slice.Included.Select(p => p.Kind));
        Assert.Equal(["signatures", "usings", "fields"], r.Slice.Omitted.Select(p => p.Kind));
        Assert.Equal(["target"], r.Regions.Select(x => x.Reason));
    }

    [Fact]
    public void FileWithoutUsings_UsesNamespaceLineAsUsingsRegion()
    {
        const string src = "\nnamespace Demo\n{\n    public class C\n    {\n        public int M() { return 1; }\n    }\n}\n";
        var target = Assert.Single(MemberLocator.FindByName(src, V, null, "M"));

        var r = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []);

        Assert.Equal("namespace Demo", Part(r, "usings").Text);
        Assert.Equal(new TextSpanInfo(2, 2), r.Regions.Single(x => x.Reason == "usings").Span);
        Assert.DoesNotContain(r.Slice.Included, p => p.Kind is "fields" or "signatures");
    }

    [Fact]
    public void FileScopedNamespace_WithoutUsings_UsesNamespaceLine()
    {
        const string src = "namespace Demo;\n\npublic class C\n{\n    public int M() => 1;\n}\n";
        var target = Assert.Single(MemberLocator.FindByName(src, V, null, "M"));

        var r = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []);

        Assert.Equal("namespace Demo;", Part(r, "usings").Text);
        Assert.Equal(new TextSpanInfo(1, 1), r.Regions.Single(x => x.Reason == "usings").Span);
    }

    [Fact]
    public void Tokens_InEachPart_EqualCounterResult()
    {
        var (src, target) = Cancel();

        var r = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []);

        Assert.All(r.Slice.Included, p => Assert.Equal(Tokens.Count(p.Text), p.Tokens));
        Assert.Equal(int.MaxValue, Part(r, "target").Priority);
        Assert.Equal(1, Part(r, "signatures").Priority);
        Assert.Equal(3, Part(r, "usings").Priority);
        Assert.Equal(4, Part(r, "fields").Priority);
    }

    [Fact]
    public void PropertiesAndExpressionBodies_AreNormalizedToOneLine()
    {
        const string src = """
            using System;
            class C
            {
                public int Auto { get; set; } = 5;
                public int Calc
                {
                    get { return 1; }
                    set { _v = value; }
                }
                public int Expr => 42;
                public int Add(int a) => a + 1;
                public void Target() { }
            }
            """;
        var target = Assert.Single(MemberLocator.FindByName(src, V, null, "Target"));

        var text = Part(RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []), "signatures").Text.Split('\n');

        Assert.Equal(
            ["public int Auto { get; set; } = 5;", "public int Calc { get { ... } set { ... } }", "public int Expr { get { ... } }", "public int Add(int a) { ... }"],
            text);
    }

    [Fact]
    public void MemberNotFoundBySpan_Throws()
    {
        var (src, target) = Cancel();

        Assert.Throws<InvalidOperationException>(() =>
            RoslynSlicer.Build(src, target with { Span = new TextSpanInfo(1, 2) }, Lang, Tokens, Huge, []));
    }

    private static string[] SignatureLines(string src, string member)
    {
        var target = MemberLocator.FindByName(src, V, null, member)[0];
        return Part(RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []), "signatures").Text.Split('\n');
    }

    [Fact]
    public void NestedTypesOfEveryKind_RenderAsBodylessOneLiners()
    {
        const string src = """
            class C
            {
                public record R(int A);
                public record R2(int A) { public int Secret = 1; }
                public struct S { public int SecretS; }
                public interface I { void SecretI(); }
                public enum E { A, B }
                public delegate int D(int x);
                public void T() { }
            }
            """;

        var lines = SignatureLines(src, "T");

        Assert.Equal(
            ["public record R(int A);", "public record R2(int A) { ... }", "public struct S { ... }",
             "public interface I { ... }", "public enum E { ... }", "public delegate int D(int x);"],
            lines);
    }

    [Fact]
    public void OtherMembers_HaveNoBodies()
    {
        const string src = """
            using System;
            class C
            {
                public int this[int i] { get { return Secret1; } set { Secret2 = value; } }
                public int this[string s] => Secret3;
                public static C operator +(C a, C b) { return Secret4; }
                public static C operator -(C a, C b) => Secret5;
                public static implicit operator int(C c) { return Secret6; }
                public static explicit operator long(C c) => Secret7;
                ~C() { Secret8(); }
                public event EventHandler Changed { add { Secret9(); } remove { Secret10(); } }
                public event EventHandler Plain;
                public void T() { }
            }
            """;

        var lines = SignatureLines(src, "T");

        Assert.Equal(
            ["public int this[int i] { get { ... } set { ... } }",
             "public int this[string s] { get { ... } }",
             "public static C operator +(C a, C b) { ... }",
             "public static C operator -(C a, C b) { ... }",
             "public static implicit operator int (C c) { ... }",
             "public static explicit operator long (C c) { ... }",
             "~C() { ... }",
             "public event EventHandler Changed { add { ... } remove { ... } }",
             "public event EventHandler Plain;"],
            lines);
    }

    [Fact]
    public void OneLineClass_TargetIsTheMemberNotTheEnclosingType()
    {
        const string src = "class C { public void T() { } public void U() { } }\n";
        var target = MemberLocator.FindByName(src, V, null, "T")[0];

        var r = RoslynSlicer.Build(src, target, Lang, Tokens, Huge, []);

        Assert.Equal("class C { public void T() { } public void U() { } }", Part(r, "target").Text);
        Assert.Equal(["public void U() { ... }"], Part(r, "signatures").Text.Split('\n'));
    }
    private const string HelperSource =
        "using System;\n" +
        "\n" +
        "class C\n" +
        "{\n" +
        "    private int _x;\n" +
        "\n" +
        "    public int T(int a)\n" +
        "    {\n" +
        "        return Twice(a);\n" +
        "    }\n" +
        "\n" +
        "    /// <summary>Doubles.</summary>\n" +
        "    private static int Twice(int v)\n" +
        "    {\n" +
        "        return v * 2;\n" +
        "    }\n" +
        "\n" +
        "    public void U() { }\n" +
        "}\n";

    [Fact]
    public void Helpers_AreVerbatimEditablePart_AndLeaveSignatures()
    {
        var target = MemberLocator.FindByName(HelperSource, V, null, "T")[0];

        var r = RoslynSlicer.Build(HelperSource, target, Lang, Tokens, Huge, ["C.Twice(int)", "C.Gone()"]);

        var helpers = Part(r, "helpers");
        Assert.Equal("    /// <summary>Doubles.</summary>\n    private static int Twice(int v)\n    {\n        return v * 2;\n    }", helpers.Text);
        Assert.Equal(5, helpers.Priority);
        Assert.Equal(new TextSpanInfo(12, 16), r.Regions.Single(x => x.Reason == "helper").Span);
        Assert.Equal(["public void U() { ... }"], Part(r, "signatures").Text.Split('\n'));
        Assert.Equal(["usings", "fields", "signatures", "helpers", "target"], r.Slice.Included.Select(p => p.Kind));
    }

    [Fact]
    public void NoHelpers_NoHelperPartOrRegion()
    {
        var target = MemberLocator.FindByName(HelperSource, V, null, "T")[0];

        var r = RoslynSlicer.Build(HelperSource, target, Lang, Tokens, Huge, []);

        Assert.DoesNotContain(r.Slice.Included, p => p.Kind == "helpers");
        Assert.DoesNotContain(r.Regions, x => x.Reason == "helper");
        Assert.Contains("private static int Twice(int v) { ... }", Part(r, "signatures").Text.Split('\n'));
    }

    [Fact]
    public void OverBudget_DropsHelpersLast_AndTheirRegion()
    {
        var target = MemberLocator.FindByName(HelperSource, V, null, "T")[0];
        var full = RoslynSlicer.Build(HelperSource, target, Lang, Tokens, Huge, ["C.Twice(int)"]);
        int T(string k) => Part(full, k).Tokens;

        var keepHelpers = RoslynSlicer.Build(HelperSource, target, Lang, Tokens, T("target") + T("helpers"), ["C.Twice(int)"]);
        var none = RoslynSlicer.Build(HelperSource, target, Lang, Tokens, T("target") + T("helpers") - 1, ["C.Twice(int)"]);

        Assert.Equal(["signatures", "usings", "fields"], keepHelpers.Slice.Omitted.Select(p => p.Kind));
        Assert.Contains(keepHelpers.Regions, x => x.Reason == "helper");
        Assert.Equal(["signatures", "usings", "fields", "helpers"], none.Slice.Omitted.Select(p => p.Kind));
        Assert.Equal(["target"], none.Regions.Select(x => x.Reason));
    }
}
