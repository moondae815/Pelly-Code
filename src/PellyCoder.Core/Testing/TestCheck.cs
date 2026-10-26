namespace PellyCoder.Core.Testing;

/// <summary>What a cs task checks for one linked test: RED before its edit, GREEN after it.</summary>
[Flags]
public enum TestCheck { None = 0, Red = 1, Green = 2 }
