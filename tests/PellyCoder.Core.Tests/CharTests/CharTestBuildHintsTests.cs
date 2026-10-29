using PellyCoder.Core.CharTests;
using Diagnostic = PellyCoder.Core.Models.Diagnostic;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class CharTestBuildHintsTests
{
    private const string TestFile = "Shop.Api.Tests/OrdersControllerTests.cs";

    private static Diagnostic Error(string code, string message, string file = TestFile) => new(file, 10, 5, "error", code, message);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Lookup(IReadOnlyCollection<string> names) =>
        names.Where(n => n is "Order" or "OrderStatus")
            .ToDictionary(n => n, IReadOnlyList<string> (_) => ["Shop.Api.Models"]);

    [Fact]
    public void MissingType_WorkspaceDeclared_SuggestsUsing()
    {
        var hints = CharTestBuildHints.For(
            [Error("CS0246", "The type or namespace name 'Order' could not be found (are you missing a using directive or an assembly reference?)"),
             Error("CS0246", "The type or namespace name 'Order' could not be found (are you missing a using directive or an assembly reference?)"),
             Error("CS0246", "The type or namespace name 'Mock<>' could not be found (are you missing a using directive or an assembly reference?)")],
            TestFile, null, Lookup);

        Assert.Equal(["Order: add `using Shop.Api.Models;`"], hints);
    }

    [Fact]
    public void MissingType_OutsideTestFile_Ignored() =>
        Assert.Empty(CharTestBuildHints.For(
            [Error("CS0246", "The type or namespace name 'Order' could not be found", "Shop.Api/X.cs")], TestFile, null, Lookup));

    [Fact]
    public void MissingInterfaceMember_AppendsDependencyLine()
    {
        const string deps = "Shop.Api.Data: interface IOrderRepository { Task<Order> GetByIdAsync(int orderId); }\n"
            + "Shop.Api.Services: interface IOrderService { Task CancelAsync(int orderId, string reason); Task<decimal> CalculateRefundAsync(int orderId); }";

        var hints = CharTestBuildHints.For(
            [Error("CS0535", "'OrdersControllerTests.FakeOrderService' does not implement interface member 'IOrderService.CalculateRefundAsync(int)'"),
             Error("CS0738", "'OrdersControllerTests.FakeOrderService' does not implement interface member 'Shop.Api.Services.IOrderService.CancelAsync(int, string)'. 'OrdersControllerTests.FakeOrderService.CancelAsync(int, string)' cannot implement 'IOrderService.CancelAsync(int, string)' because it does not have the matching return type of 'Task'.")],
            TestFile, deps, Lookup);

        var hint = Assert.Single(hints);
        Assert.Equal("Implement every member of IOrderService (unused ones: => throw new System.NotImplementedException()): "
            + "Shop.Api.Services: interface IOrderService { Task CancelAsync(int orderId, string reason); Task<decimal> CalculateRefundAsync(int orderId); }", hint);
    }
}
