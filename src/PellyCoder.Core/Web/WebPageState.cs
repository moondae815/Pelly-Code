using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Web;

/// <summary>The page side of a <c>pelly js</c> / <c>pelly css</c> run on a page: the original page, the target block and every block's view.</summary>
public sealed record WebPageState(AspPage OriginalPage, int BlockIndex, PageBlockView OriginalView, IReadOnlyList<PageBlockView> OriginalViews);
