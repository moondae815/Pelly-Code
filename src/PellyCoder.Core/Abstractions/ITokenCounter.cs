namespace PellyCoder.Core.Abstractions;

/// <summary>Counts tokens the same way the local model server does (input budget basis).</summary>
public interface ITokenCounter
{
    int Count(string text);
}
