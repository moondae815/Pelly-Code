namespace PellyCoder.Core.Specs;

/// <summary>The one rule that turns a spec task symbol into a class filter and a member name.</summary>
public static class SpecSymbol
{
    /// <summary>
    /// <c>Cancel</c> → (null, Cancel); <c>OrderService.Cancel</c> → (OrderService, Cancel);
    /// <c>Outer.Inner.M</c> → (Inner, M): the last segment is the member, the one before it the class.
    /// </summary>
    public static (string? ClassName, string Member) Split(string symbol)
    {
        var parts = symbol.Split('.');
        return parts.Length == 1 ? (null, parts[0]) : (parts[^2], parts[^1]);
    }
}
