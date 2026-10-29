using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CharTests;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class CharTestOutputParserTests
{
    private const LanguageVersion V = LanguageVersion.CSharp12;

    [Fact]
    public void Parse_UsingsAndMembers()
    {
        var (o, e) = CharTestOutputParser.Parse(
            "### USINGS\nusing System;\nusing Xunit;\n### MEMBERS\n[Fact]\npublic void A() { }\n\nprivate int B() => 1;\n", V);
        Assert.Null(e);
        Assert.Equal(["using System;", "using Xunit;"], o!.Usings);
        Assert.Equal(2, o.Members.Count);
        Assert.Contains("void A()", o.Members[0]);
        Assert.Contains("int B()", o.Members[1]);
    }

    [Fact]
    public void Parse_NoUsings_Ok()
    {
        var (o, e) = CharTestOutputParser.Parse("### USINGS\n### MEMBERS\npublic void A() { }", V);
        Assert.Null(e);
        Assert.Empty(o!.Usings);
        Assert.Single(o.Members);
    }

    [Fact]
    public void Parse_MissingMembersHeader_Error()
    {
        var (o, e) = CharTestOutputParser.Parse("### USINGS\nusing System;\n", V);
        Assert.Null(o);
        Assert.Contains("MEMBERS", e);
    }

    [Fact]
    public void Parse_StripsCodeFences()
    {
        var (o, e) = CharTestOutputParser.Parse(
            "### USINGS\n```csharp\nusing System;\n```\n### MEMBERS\n```csharp\npublic void A() { }\n```\n", V);
        Assert.Null(e);
        Assert.Single(o!.Usings);
        Assert.Single(o.Members);
        Assert.DoesNotContain("```", o.Members[0]);
    }

    [Fact]
    public void Parse_SyntaxError_Error()
    {
        var (o, e) = CharTestOutputParser.Parse("### USINGS\n### MEMBERS\npublic void A( { }", V);
        Assert.Null(o);
        Assert.False(string.IsNullOrWhiteSpace(e));
    }

    [Fact]
    public void Parse_NonUsingLine_Error()
    {
        var (o, e) = CharTestOutputParser.Parse("### USINGS\nvar x = 1;\n### MEMBERS\n", V);
        Assert.Null(o);
        Assert.NotNull(e);
    }

    [Fact]
    public void Parse_UsingWithTrailingComment_AndCommentLine_Ok()
    {
        var (o, e) = CharTestOutputParser.Parse(
            "### USINGS\nusing System; // for DateTime\n// the models\nusing Shop.Api.Models;   /* ok */\n### MEMBERS\npublic void A() { }", V);
        Assert.Null(e);
        Assert.Equal(["using System;", "using Shop.Api.Models;"], o!.Usings);
    }

    [Fact]
    public void Parse_AliasUsing_Ok()
    {
        var (o, e) = CharTestOutputParser.Parse("### USINGS\nusing M = Shop.Api.Models;\nusing L = System.Collections.Generic.List<int>;\n### MEMBERS\npublic void A() { }", V);
        Assert.Null(e);
        Assert.Equal(["using M = Shop.Api.Models;", "using L = System.Collections.Generic.List<int>;"], o!.Usings);
    }

    [Fact]
    public void Parse_Prose_StillRejected()
    {
        var (o, e) = CharTestOutputParser.Parse("### USINGS\nusing these namespaces the tests compile;\n### MEMBERS\npublic void A() { }", V);
        Assert.Null(o);
        Assert.NotNull(e);
    }
}
