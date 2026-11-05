using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Rules;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Verification;

public sealed class RulesStepTests
{
    private const string Path = "/w/a.js";
    private static readonly ProjectRule NoEval = new(1, ["*"], "no eval", "eval\\(");

    private static RunContext Context(string original, string current, EditKind kind)
    {
        var fs = new MockFileSystem();
        var guard = new FileGuard(fs, TimeProvider.System, new WebTextCodec("cp949"));
        fs.AddFile(Path, new MockFileData(Encoding.UTF8.GetBytes(original)));
        var ctx = new RunContext("r1", new RunRequest(Path, null, null, null, "x", true, false, null, Kind: kind), new PellyOptions())
        {
            FilePath = Path,
            Rules = [NoEval],
            Target = new EditTarget(kind, Path, "f", new TextSpanInfo(1, 1), null),
        };
        ctx.Original = guard.Read(Path);
        fs.AddFile(Path, new MockFileData(Encoding.UTF8.GetBytes(current)));
        ctx.Current = guard.Read(Path);
        return ctx;
    }

    private static Task<StepResult> Run(RunContext ctx) =>
        new RulesStep(new FeedbackBuilder(QwenTokenCounter.Shared, new BudgetOptions().Feedback)).VerifyAsync(ctx, CancellationToken.None);

    [Fact]
    public async Task Rules_Js_StringsMasked()
    {
        var ctx = Context("var a = 1;\n", "var a = 1;\nvar s = \"eval(x)\"; // eval(y)\n", EditKind.Js);

        var result = await Run(ctx);

        Assert.Equal(StepResult.Pass, result);
    }

    [Fact]
    public async Task Rules_Js_CodeViolation()
    {
        var ctx = Context("var a = 1;\n", "var a = 1;\neval(x);\n", EditKind.Js);

        var result = await Run(ctx);

        Assert.NotEqual(StepResult.Pass, result);
    }

    [Fact]
    public async Task Rules_Css_CommentMasked()
    {
        var ctx = Context("a { color: red; }\n", "a { color: red; }\n/* eval(x) */\n", EditKind.Css);

        var result = await Run(ctx);

        Assert.Equal(StepResult.Pass, result);
    }
}
