namespace PellyCoder.Core.Files;

/// <summary>
/// How <see cref="FileGuard"/> turns disk bytes into a <see cref="FileSnapshot"/> and back.
/// <see cref="Encode"/> throws <see cref="UnencodableTextException"/> for characters the file encoding cannot hold;
/// <see cref="Decode"/> throws <see cref="InvalidDataException"/> (Korean message) when the bytes cannot be read.
/// </summary>
public interface ITextFileCodec
{
    FileSnapshot Decode(string path, byte[] bytes);

    byte[] Encode(FileSnapshot like, string textLf);

    /// <summary>The snapshot after <paramref name="written"/> (from <see cref="Encode"/>) replaced <paramref name="current"/> on disk.</summary>
    FileSnapshot AfterWrite(FileSnapshot current, string newTextLf, byte[] written);
}

/// <summary>The shared codec of <c>cs</c>/<c>sql</c> edits: <see cref="TextFileCodec"/> unchanged.</summary>
public sealed class DefaultTextFileCodec : ITextFileCodec
{
    public static readonly DefaultTextFileCodec Instance = new();

    private DefaultTextFileCodec() { }

    public FileSnapshot Decode(string path, byte[] bytes) => TextFileCodec.Decode(path, bytes);

    public byte[] Encode(FileSnapshot like, string textLf) => TextFileCodec.Encode(like, textLf);

    public FileSnapshot AfterWrite(FileSnapshot current, string newTextLf, byte[] written) => current with
    {
        Sha256 = TextFileCodec.HashHex(written),
        Text = TextFileCodec.NormalizeTail(current, newTextLf),
    };
}
