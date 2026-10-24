using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CSharp;

public sealed class MemberShapeGuardTests
{
    private const LanguageVersion V = LanguageVersion.CSharp12;
    private const string TargetDisplay = "Calc.Add(int, int)";

    private const string Before =
        "using System;\n" +
        "using System.Linq;\n" +
        "\n" +
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    private int _count;\n" +
        "\n" +
        "    /// <summary>Adds.</summary>\n" +
        "    [Obsolete]\n" +
        "    public int Add(int a, int b)\n" +
        "    {\n" +
        "        return a + b;\n" +
        "    }\n" +
        "\n" +
        "    public T Echo<T>(T value) where T : class\n" +
        "    {\n" +
        "        return value;\n" +
        "    }\n" +
        "\n" +
        "    public int Sub(int a, int b)\n" +
        "    {\n" +
        "        return a - b;\n" +
        "    }\n" +
        "}\n";

    private const string AddHeader = "    public int Add(int a, int b)\n";
    private const string AddBody = "        return a + b;\n";
    private const string AddEnd = "        return a + b;\n    }\n";

    private static ShapeCheck Check(string after, string target = TargetDisplay) =>
        MemberShapeGuard.Check(Before, after, target, V);

    private static string Replace(string oldText, string newText)
    {
        Assert.Contains(oldText, Before);
        return Before.Replace(oldText, newText);
    }

    /// <summary>Inserts text right after the target member's closing brace.</summary>
    private static string AfterTarget(string text) => Replace(AddEnd, AddEnd + text);

    [Fact]
    public void BodyChangeOnly_IsOk()
    {
        var r = Check(Replace(AddBody, "        if (a < 0) throw new ArgumentOutOfRangeException(nameof(a));\n        return a + b; // sum\n"));

        Assert.True(r.Ok, r.Reason);
        Assert.Null(r.Reason);
        Assert.Empty(r.AddedPrivateMethods);
    }

    [Fact]
    public void DocCommentAndParameterNameChange_IsOk()
    {
        var r = Check(Replace("    /// <summary>Adds.</summary>\n    [Obsolete]\n" + AddHeader, "    /// <summary>Adds two numbers.</summary>\n    [Obsolete]\n    public int Add(int x, int y)\n")
            .Replace(AddBody, "        return x + y;\n"));

        Assert.True(r.Ok, r.Reason);
    }

    [Theory]
    [InlineData("add parameter")]
    [InlineData("change parameter type")]
    [InlineData("ref to out")]
    [InlineData("default value")]
    [InlineData("return type")]
    [InlineData("public to internal")]
    [InlineData("add static")]
    [InlineData("generic constraint")]
    public void SignatureChanges_AreRejected(string variant)
    {
        var (before, after, target) = variant switch
        {
            "add parameter" => (Before, Replace(AddHeader, "    public int Add(int a, int b, bool checkedMath)\n"), TargetDisplay),
            "change parameter type" => (Before, Replace(AddHeader, "    public int Add(long a, int b)\n"), TargetDisplay),
            "ref to out" => (Before.Replace(AddHeader, "    public int Add(ref int a, int b)\n"),
                Before.Replace(AddHeader, "    public int Add(out int a, int b)\n"), TargetDisplay),
            "default value" => (Before, Replace(AddHeader, "    public int Add(int a, int b = 1)\n"), TargetDisplay),
            "return type" => (Before, Replace(AddHeader, "    public long Add(int a, int b)\n"), TargetDisplay),
            "public to internal" => (Before, Replace(AddHeader, "    internal int Add(int a, int b)\n"), TargetDisplay),
            "add static" => (Before, Replace(AddHeader, "    public static int Add(int a, int b)\n"), TargetDisplay),
            "generic constraint" => (Before, Replace("where T : class", "where T : class, new()"), "Calc.Echo(T)"),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        var r = MemberShapeGuard.Check(before, after, target, V);

        Assert.False(r.Ok);
        Assert.StartsWith("signature of " + target + " changed ", r.Reason);
        Assert.EndsWith(". Signature changes are not allowed.", r.Reason);
    }

    [Fact]
    public void AsyncToggle_SameReturnType_IsOk()
    {
        const string before = "using System.Threading.Tasks;\nclass S\n{\n    public Task<int> Get()\n    {\n        return Task.FromResult(1);\n    }\n}\n";
        const string after = "using System.Threading.Tasks;\nclass S\n{\n    public async Task<int> Get()\n    {\n        await Task.Yield();\n        return 1;\n    }\n}\n";

        Assert.True(MemberShapeGuard.Check(before, after, "S.Get()", V).Ok);
        Assert.True(MemberShapeGuard.Check(after, before, "S.Get()", V).Ok);
    }

    [Fact]
    public void AsyncToggle_ChangesReturnType_IsRejected()
    {
        const string before = "using System.Threading.Tasks;\nclass S\n{\n    public int Get()\n    {\n        return 1;\n    }\n}\n";
        const string after = "using System.Threading.Tasks;\nclass S\n{\n    public async Task<int> Get()\n    {\n        await Task.Yield();\n        return 1;\n    }\n}\n";

        var r = MemberShapeGuard.Check(before, after, "S.Get()", V);

        Assert.False(r.Ok);
        Assert.Equal("signature of S.Get() changed from `public int Get()` to `public async Task<int> Get()`. Signature changes are not allowed.", r.Reason);
    }

    [Fact]
    public void AttributeChange_IsRejected()
    {
        var removed = Check(Replace("    [Obsolete]\n", ""));
        var changed = Check(Replace("    [Obsolete]\n", "    [Obsolete(\"use Sum\")]\n"));

        Assert.False(removed.Ok);
        Assert.Equal("attributes of Calc.Add(int, int) changed. Attribute changes are not allowed.", removed.Reason);
        Assert.False(changed.Ok);
        Assert.Equal("attributes of Calc.Add(int, int) changed. Attribute changes are not allowed.", changed.Reason);
    }

    [Fact]
    public void OtherMemberRemoved_IsRejected()
    {
        var r = Check(Replace("\n    public int Sub(int a, int b)\n    {\n        return a - b;\n    }\n", ""));

        Assert.False(r.Ok);
        Assert.Equal("member Calc.Sub(int, int) was removed or changed. Only the target member may change.", r.Reason);
    }

    [Fact]
    public void OtherMemberChanged_IsRejected()
    {
        var body = Check(Replace("return a - b;", "return b - a;"));
        var field = Check(Replace("private int _count;", "private int _count = 1;"));
        var whitespaceOnly = Check(Replace("return a - b;", "return  a -   b;"));

        Assert.Equal("member Calc.Sub(int, int) was removed or changed. Only the target member may change.", body.Reason);
        Assert.Equal("member Calc._count was removed or changed. Only the target member may change.", field.Reason);
        Assert.True(whitespaceOnly.Ok, whitespaceOnly.Reason);
    }

    [Fact]
    public void AddedPrivateMethod_IsOk_AndListed()
    {
        var r = Check(AfterTarget("\n    private static int Twice(int value)\n    {\n        return value * 2;\n    }\n\n    private void Log() { }\n"));

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(["Calc.Twice(int)", "Calc.Log()"], r.AddedPrivateMethods);
    }

    [Fact]
    public void AddedPrivateOverloadOfTarget_IsOk()
    {
        var r = Check(AfterTarget("\n    private int Add(int a)\n    {\n        return a;\n    }\n"));

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(["Calc.Add(int)"], r.AddedPrivateMethods);
    }

    [Fact]
    public void AddedImplicitlyPrivateMethod_InClass_IsOk_AndListed()
    {
        var r = Check(AfterTarget("\n    static int Plus(int a, int b) => a + b;\n"));

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(["Calc.Plus(int, int)"], r.AddedPrivateMethods);
    }

    [Fact]
    public void AddedMethodWithoutModifier_InInterface_IsRejected()
    {
        const string before = "interface IShape\n{\n    int Area();\n}\n\nclass Sq\n{\n    public int Run() { return 1; }\n}\n";
        var after = before.Replace("    int Area();\n", "    int Area();\n    int Twice() => 2;\n");

        var r = MemberShapeGuard.Check(before, after, "Sq.Run()", V);

        Assert.False(r.Ok);
        Assert.Equal("added method IShape.Twice(). Only private helper methods may be added.", r.Reason);
    }

    [Fact]
    public void AddedMethodWithoutModifier_InTargetInterface_IsRejected()
    {
        const string before = "interface IShape\n{\n    int Area() { return 1; }\n}\n";
        var after = before.Replace("    int Area() { return 1; }\n", "    int Area() { return Twice(); }\n    int Twice() => 2;\n");

        var r = MemberShapeGuard.Check(before, after, "IShape.Area()", V);

        Assert.False(r.Ok);
        Assert.Equal("added method IShape.Twice(). Only private helper methods may be added.", r.Reason);
    }

    [Fact]
    public void ReturnTypeChange_ReasonShowsHeaders()
    {
        var r = Check(Replace(AddHeader, "    public long Add(int a, int b)\n"));

        Assert.Equal("signature of Calc.Add(int, int) changed from `public int Add(int a, int b)` to `public long Add(int a, int b)`. Signature changes are not allowed.",
            r.Reason);
    }

    [Fact]
    public void SameDisplayHeaders_ExcludeAttributesAndBody()
    {
        var r = Check(Replace(AddHeader + "    {\n" + AddEnd, "    public static int Add(int a, int b = 2) => a + b;\n"));

        Assert.Equal("signature of Calc.Add(int, int) changed from `public int Add(int a, int b)` to `public static int Add(int a, int b = 2)`. Signature changes are not allowed.",
            r.Reason);
    }

    [Fact]
    public void AddedPublicMethod_IsRejected()
    {
        var pub = Check(AfterTarget("\n    public int Twice(int value) => value * 2;\n"));
        var privateProtected = Check(AfterTarget("\n    private protected int Twice(int value) => value * 2;\n"));

        Assert.False(pub.Ok);
        Assert.Equal("added method Calc.Twice(int). Only private helper methods may be added.", pub.Reason);
        Assert.False(privateProtected.Ok);
    }

    [Fact]
    public void AddedPrivateMethodInAnotherType_IsRejected()
    {
        var r = Check(Before + "\npublic class Other\n{\n    private void Helper() { }\n}\n");
        const string other = "\npublic class Other\n{\n    public void Run() { }\n}\n";
        var existing = MemberShapeGuard.Check(Before + other, Before + other.Replace("    public void Run() { }\n", "    public void Run() { }\n    private void Helper() { }\n"), TargetDisplay, V);

        Assert.False(r.Ok);
        Assert.Equal("added class Other. Only private helper methods may be added.", r.Reason);
        Assert.False(existing.Ok);
        Assert.Equal("added method Other.Helper(). Only private helper methods may be added.", existing.Reason);
    }

    [Fact]
    public void AddedField_IsRejected()
    {
        var field = Check(AfterTarget("\n    private int _extra;\n"));
        var property = Check(AfterTarget("\n    private int Extra { get; set; }\n"));

        Assert.False(field.Ok);
        Assert.Equal("added field Calc._extra. Only private helper methods may be added.", field.Reason);
        Assert.False(property.Ok);
        Assert.Equal("added property Calc.Extra. Only private helper methods may be added.", property.Reason);
    }

    [Fact]
    public void AddedNestedType_IsRejected()
    {
        var r = Check(AfterTarget("\n    private sealed class Cache\n    {\n    }\n"));

        Assert.False(r.Ok);
        Assert.Equal("added class Calc.Cache. Only private helper methods may be added.", r.Reason);
    }

    [Fact]
    public void UsingAdded_IsOk()
    {
        var r = Check(Replace("using System.Linq;\n", "using System.Linq;\nusing System.Text;\n"));

        Assert.True(r.Ok, r.Reason);
    }

    [Fact]
    public void UsingRemoved_IsRejected()
    {
        var r = Check(Replace("using System.Linq;\n", ""));

        Assert.False(r.Ok);
        Assert.Equal("using System.Linq was removed. Usings may only be added.", r.Reason);
    }

    [Fact]
    public void UsingReordered_IsOk()
    {
        var r = Check(Replace("using System;\nusing System.Linq;\n", "using System.Linq;\nusing System;\n"));

        Assert.True(r.Ok, r.Reason);
    }

    [Fact]
    public void HelperFromEarlierAttempt_MayChangeOrDisappear()
    {
        // APPLY compares against the original file, so a helper added on attempt 1 is still "added" on attempt 2.
        var attempt2 = AfterTarget("\n    private static int Twice(int value)\n    {\n        return value + value;\n    }\n");

        var r = Check(attempt2);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(["Calc.Twice(int)"], r.AddedPrivateMethods);
    }

    [Fact]
    public void PartialClassAcrossFile_IsSameType()
    {
        const string before = "partial class P\n{\n    public void A() { }\n}\n\npartial class P\n{\n    public void B() { }\n}\n";
        var after = before.Replace("public void A() { }", "public void A() { B(); }\n\n    private void H() { }");

        var r = MemberShapeGuard.Check(before, after, "P.A()", V);

        Assert.True(r.Ok, r.Reason);
        Assert.Equal(["P.H()"], r.AddedPrivateMethods);
    }

    [Fact]
    public void PropertyTarget_TypeChange_IsRejected_BodyChange_IsOk()
    {
        const string before = "class S\n{\n    private int _v;\n    public int V\n    {\n        get { return _v; }\n    }\n}\n";
        var body = before.Replace("get { return _v; }", "get { return _v + 1; }");
        var type = before.Replace("public int V", "public long V");
        var setter = before.Replace("get { return _v; }", "get { return _v; }\n        set { _v = value; }");

        Assert.True(MemberShapeGuard.Check(before, body, "S.V", V).Ok);
        Assert.Equal("signature of S.V changed from `public int V { get; }` to `public long V { get; }`. Signature changes are not allowed.",
            MemberShapeGuard.Check(before, type, "S.V", V).Reason);
        Assert.Equal("signature of S.V changed from `public int V { get; }` to `public int V { get; set; }`. Signature changes are not allowed.",
            MemberShapeGuard.Check(before, setter, "S.V", V).Reason);
    }

    [Fact]
    public void TargetRemoved_IsRejected()
    {
        var r = Check(Replace("    /// <summary>Adds.</summary>\n    [Obsolete]\n" + AddHeader + "    {\n" + AddEnd, ""));

        Assert.False(r.Ok);
        Assert.Equal("member Calc.Add(int, int) was removed or changed. Only the target member may change.", r.Reason);
    }

    [Fact]
    public void ReasonText_IsExact()
    {
        Assert.Equal("signature of Calc.Add(int, int) changed to Calc.Add(int, int, bool). Signature changes are not allowed.",
            Check(Replace(AddHeader, "    public int Add(int a, int b, bool c)\n")).Reason);
        Assert.Equal("attributes of Calc.Add(int, int) changed. Attribute changes are not allowed.",
            Check(Replace("    [Obsolete]\n", "    [Obsolete, Serializable]\n")).Reason);
        Assert.Equal("member Calc.Echo(T) was removed or changed. Only the target member may change.",
            Check(Replace("return value;", "return null!;")).Reason);
        Assert.Equal("added method Calc.Helper(). Only private helper methods may be added.",
            Check(AfterTarget("\n    internal void Helper() { }\n")).Reason);
        Assert.Equal("using System was removed. Usings may only be added.",
            Check(Replace("using System;\n", "")).Reason);
    }

    private const string Money =
        "public readonly struct Money\n{\n    public decimal Value { get; init; }\n\n" +
        "    public static Money operator +(Money a, Money b)\n    {\n        return new Money { Value = a.Value + b.Value };\n    }\n\n" +
        "    public static implicit operator decimal(Money m)\n    {\n        return m.Value;\n    }\n}\n";

    [Fact]
    public void OperatorTarget_BodyChange_IsOk()
    {
        var plus = MemberShapeGuard.Check(Money, Money.Replace("a.Value + b.Value", "Math.Round(a.Value + b.Value, 2)"),
            "Money.operator +(Money, Money)", V);
        var conversion = MemberShapeGuard.Check(Money, Money.Replace("return m.Value;", "return Math.Round(m.Value, 2);"),
            "Money.operator decimal(Money)", V);

        Assert.True(plus.Ok, plus.Reason);
        Assert.True(conversion.Ok, conversion.Reason);
    }

    [Fact]
    public void OperatorTarget_SignatureChange_IsRejected()
    {
        var plus = MemberShapeGuard.Check(Money, Money.Replace("operator +(Money a, Money b)", "operator +(Money a, decimal b)"),
            "Money.operator +(Money, Money)", V);
        var conversion = MemberShapeGuard.Check(Money, Money.Replace("implicit operator decimal", "explicit operator decimal"),
            "Money.operator decimal(Money)", V);

        Assert.Equal("signature of Money.operator +(Money, Money) changed to Money.operator +(Money, decimal). Signature changes are not allowed.",
            plus.Reason);
        Assert.False(conversion.Ok);
        Assert.StartsWith("signature of Money.operator decimal(Money) changed from ", conversion.Reason);
    }

    private const string Orders =
        "using System.Threading.Tasks;\n\nnamespace Demo;\n\n" +
        "public interface IOrders\n{\n    OrderDto GetOrder(int id);\n\n    void Close(int id);\n}\n\n" +
        "public class OrderService\n{\n    public Task Cancel(int id)\n    {\n        return Task.CompletedTask;\n    }\n\n" +
        "    public int Count() => 0;\n}\n";

    private static ShapeCheck Group(string after, string target) =>
        MemberShapeGuard.Check(Orders, after, target, V, ShapeAllowance.TargetSignatureAndName);

    private static string OrdersReplace(string oldText, string newText)
    {
        Assert.Contains(oldText, Orders);
        return Orders.Replace(oldText, newText);
    }

    [Fact]
    public void Group_allows_return_type_change_and_reports_new_display()
    {
        var r = Group(OrdersReplace("    public Task Cancel(int id)\n    {\n        return Task.CompletedTask;",
            "    public Task<decimal> Cancel(int id)\n    {\n        return Task.FromResult(0m);"), "OrderService.Cancel(int)");

        Assert.True(r.Ok, r.Reason);
        Assert.Equal("OrderService.Cancel(int)", r.NewTargetDisplay);
    }

    [Fact]
    public void Group_allows_rename_in_place()
    {
        var r = Group(OrdersReplace("OrderDto GetOrder(int id);", "OrderDto FindOrder(int id);"), "IOrders.GetOrder(int)");

        Assert.True(r.Ok, r.Reason);
        Assert.Equal("IOrders.FindOrder(int)", r.NewTargetDisplay);
    }

    [Fact]
    public void Group_allows_rename_with_parameter_change()
    {
        var r = Group(OrdersReplace("OrderDto GetOrder(int id);", "OrderDto FindOrder(long id, bool deep);"), "IOrders.GetOrder(int)");

        Assert.True(r.Ok, r.Reason);
        Assert.Equal("IOrders.FindOrder(long, bool)", r.NewTargetDisplay);
    }

    [Fact]
    public void Group_allows_parameter_change_with_helper_added_above()
    {
        var r = Group(OrdersReplace("    public Task Cancel(int id)\n",
            "    private static void Log(long id) { }\n\n    public Task Cancel(long id)\n"), "OrderService.Cancel(int)");

        Assert.True(r.Ok, r.Reason);
        Assert.Equal("OrderService.Cancel(long)", r.NewTargetDisplay);
        Assert.Equal(["OrderService.Log(long)"], r.AddedPrivateMethods);
    }

    private const string OrdersWithHelper =
        "namespace Demo;\n\npublic class OrderService\n{\n    public long Cancel(int id)\n    {\n        return Round(id);\n    }\n\n" +
        "    private long Round(int id)\n    {\n        return id;\n    }\n\n    public int Count() => 0;\n}\n";

    [Fact]
    public void Group_rerun_may_change_the_helpers_the_task_added()
    {
        var after = OrdersWithHelper.Replace("    private long Round(int id)\n    {\n        return id;", "    private long Round(long id)\n    {\n        return id * 2;");

        var allowed = MemberShapeGuard.Check(OrdersWithHelper, after, "OrderService.Cancel(int)", V, ShapeAllowance.TargetSignatureAndName,
            ["OrderService.Round(int)"]);
        var notListed = MemberShapeGuard.Check(OrdersWithHelper, after, "OrderService.Cancel(int)", V, ShapeAllowance.TargetSignatureAndName);

        Assert.True(allowed.Ok, allowed.Reason);
        Assert.Equal(["OrderService.Round(long)"], allowed.AddedPrivateMethods);
        Assert.False(notListed.Ok);
        Assert.Equal("member OrderService.Round(int) was removed or changed. Only the target member may change.", notListed.Reason);
    }

    [Fact]
    public void Editable_helpers_never_open_a_public_member()
    {
        var after = OrdersWithHelper.Replace("public int Count() => 0;", "public int Count() => 1;");

        var r = MemberShapeGuard.Check(OrdersWithHelper, after, "OrderService.Cancel(int)", V, ShapeAllowance.TargetSignatureAndName,
            ["OrderService.Count()"]);

        Assert.False(r.Ok);
        Assert.Equal("member OrderService.Count() was removed or changed. Only the target member may change.", r.Reason);
    }

    [Fact]
    public void Group_still_rejects_other_member_change()
    {
        var r = Group(OrdersReplace("OrderDto GetOrder(int id);", "OrderDto FindOrder(int id);")
            .Replace("void Close(int id);", "void Close(long id);"), "IOrders.GetOrder(int)");

        Assert.False(r.Ok);
        Assert.Equal("member IOrders.Close(int) was removed or changed. Only the target member may change.", r.Reason);
        Assert.Null(r.NewTargetDisplay);
    }

    [Fact]
    public void Group_rejects_rename_plus_moved_member()
    {
        var r = Group(OrdersReplace("    OrderDto GetOrder(int id);\n\n    void Close(int id);",
            "    void Close(int id);\n\n    OrderDto FindOrder(int id);"), "IOrders.GetOrder(int)");

        Assert.False(r.Ok);
        Assert.Equal("member IOrders.GetOrder(int) was removed or changed. Only the target member may change.", r.Reason);
    }

    [Fact]
    public void Group_still_rejects_public_addition()
    {
        var r = Group(OrdersReplace("    public int Count() => 0;\n", "    public int Count() => 0;\n\n    public void Extra() { }\n"),
            "OrderService.Cancel(int)");

        Assert.False(r.Ok);
        Assert.Equal("added method OrderService.Extra(). Only private helper methods may be added.", r.Reason);
    }

    [Fact]
    public void Default_allowance_still_rejects_signature_change()
    {
        var after = Replace(AddHeader, "    public long Add(int a, int b)\n");

        var r = MemberShapeGuard.Check(Before, after, TargetDisplay, V, ShapeAllowance.MemberBodyOnly);

        Assert.Equal("signature of Calc.Add(int, int) changed from `public int Add(int a, int b)` to `public long Add(int a, int b)`. Signature changes are not allowed.",
            r.Reason);
        Assert.Null(r.NewTargetDisplay);
        Assert.Equal(TargetDisplay, Check(Replace(AddBody, "        return b + a;\n")).NewTargetDisplay);
    }
}
