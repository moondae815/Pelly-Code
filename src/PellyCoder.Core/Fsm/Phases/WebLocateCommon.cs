using PellyCoder.Core.Files;
using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>Parts JS and CSS LOCATE share: file refusals, site setup, selection and failure helpers.</summary>
internal static class WebLocateCommon
{
    /// <summary>
    /// Refuses a file without <paramref name="extension"/> (<c>WebUnsupportedFile</c>), a minified file and a vendor file;
    /// null when the file may be edited. A page is accepted as it is: no minified or vendor filter applies to it.
    /// </summary>
    public static PhaseOutcome? RefuseFile(IFileSystem fs, RunContext ctx, PellyOptions options, string siteRoot, string extension,
        string command, bool acceptPages = false)
    {
        if (acceptPages && WebPages.IsPage(ctx.FilePath, options.Asp.Extensions))
            return null;
        if (!fs.Path.GetExtension(ctx.FilePath).Equals(extension, StringComparison.OrdinalIgnoreCase))
            return Refuse(ctx, FailureKinds.WebUnsupportedFile, $"{command}는 {extension} 파일만 고칩니다: {ctx.FilePath}");
        var (verdict, reason) = new WebFileFilter(siteRoot, options.Web.VendorPaths).Classify(ctx.FilePath, ctx.Original.Text);
        return verdict switch
        {
            WebFileVerdict.Minified => Refuse(ctx, FailureKinds.WebMinifiedFile, $"{reason}: {ctx.FilePath}"),
            WebFileVerdict.Vendor => Refuse(ctx, FailureKinds.WebVendorFile, $"{reason}: {ctx.FilePath}"),
            _ => null,
        };
    }

    public static string SiteRoot(IFileSystem fs, RunContext ctx, PellyOptions options) =>
        AspSiteRoot.Resolve(fs, options.Asp.SiteRoot, ctx.Workspace!.Root);

    /// <summary>The site index, built on first use and cached at <c>&lt;workspace root&gt;/.pelly/cache/web-site-index.json</c>.</summary>
    public static Lazy<WebSiteIndexResult> SiteIndex(IFileSystem fs, RunContext ctx, PellyOptions options, string siteRoot, ITextFileCodec codec)
    {
        var cachePath = fs.Path.Combine(ctx.Workspace!.Root, ".pelly", "cache", "web-site-index.json");
        var filter = new WebFileFilter(siteRoot, options.Web.VendorPaths);
        return new Lazy<WebSiteIndexResult>(() => WebSiteIndex.Load(fs, siteRoot, filter, codec, cachePath, aspExtensions: options.Asp.Extensions,
            spCallFunctions: options.Asp.SpCallFunctions));
    }

    /// <summary>Every byte of the original file is below 0x80 (a BOM is not).</summary>
    public static bool OriginalAscii(IFileSystem fs, RunContext ctx)
    {
        var bytes = ctx.Original.Raw ?? fs.File.ReadAllBytes(ctx.FilePath);
        return bytes.AsSpan().IndexOfAnyExceptInRange((byte)0, (byte)0x7F) < 0;
    }

    /// <summary>One candidate is the target; several go to the selection UI, or fail as ambiguous when not interactive.</summary>
    public static async Task<T?> ChooseAsync<T>(IUserInteraction ui, RunContext ctx, IReadOnlyList<T> candidates, string title,
        Func<T, string> label, string ambiguousMessage, CancellationToken ct) where T : class
    {
        if (candidates.Count == 1)
            return candidates[0];
        if (!ui.IsInteractive)
        {
            ctx.Messages.Add(ambiguousMessage + string.Join(", ", candidates.Select(label)));
            ctx.FailureKind = FailureKinds.TargetAmbiguous;
            return null;
        }
        return await ui.SelectAsync(title, candidates, label, ct);
    }

    public static int LineCount(string textLf)
    {
        var count = textLf.Split('\n').Length;
        return textLf.EndsWith('\n') ? count - 1 : count;
    }

    public static bool OutOfFile(TextSpanInfo lines, int lineCount) =>
        lines.StartLine < 1 || lines.EndLine < lines.StartLine || lines.EndLine > lineCount;

    public static string OutOfFileMessage(TextSpanInfo lines, int lineCount) =>
        $"줄 {lines.StartLine}-{lines.EndLine} 범위가 파일(1-{lineCount}줄) 밖입니다";

    public static string EncodingName(Encoding encoding) => encoding.CodePage switch
    {
        949 => "CP949",
        65001 => "UTF-8",
        _ => encoding.WebName.ToUpperInvariant(),
    };

    public static PhaseOutcome Refuse(RunContext ctx, string failureKind, string message)
    {
        ctx.Messages.Add(message);
        ctx.FailureKind = failureKind;
        return PhaseOutcome.NotFound;
    }

    public static PhaseOutcome NotFound(RunContext ctx, string message) => Refuse(ctx, FailureKinds.TargetNotFound, message);

    /// <summary>The outcome after <see cref="ChooseAsync{T}"/> gave no target.</summary>
    public static PhaseOutcome NoChoice(RunContext ctx) =>
        ctx.FailureKind == FailureKinds.TargetAmbiguous ? PhaseOutcome.Ambiguous : PhaseOutcome.NotFound;
}
