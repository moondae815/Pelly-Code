using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Llm;

/// <summary>
/// Asks the model which member a request is about when the request names none (design 8.4, step 5). The answer must be
/// a member from the list; anything else is null. Server failures surface as <see cref="LlmUnavailableException"/>.
/// </summary>
public sealed class ModelMemberPicker(ILlmBackend backend, QwenChatTemplate template, PromptResources prompts, LlmOptions llm,
    SamplingSet sampling)
{
    /// <summary>The real length cap: the engine ignores <c>stop</c> (D23), which is sent anyway.</summary>
    private const int NumPredict = 32;

    public async Task<MemberCandidate?> PickAsync(IReadOnlyList<MemberCandidate> members, string request, CancellationToken ct)
    {
        var user = new StringBuilder("### MEMBERS\n");
        foreach (var member in members)
            user.Append("- ").Append(member.Display).Append('\n');
        user.Append("### REQUEST\n").Append(request);

        var raw = template.Build(prompts.Locate, user.ToString()) + template.EmptyThink;
        var req = new LlmRequest(raw, sampling.Code, NumPredict, [template.EndOfTurn], llm.NumCtx, llm.KeepAlive);
        var (text, _) = await QwenStreamClient.ReadAsync(backend, req, TimeProvider.System, ct);
        return Match(members, Answer(text));
    }

    /// <summary>First non-empty line before the end-of-turn token, trimmed, without a leading list dash.</summary>
    private string Answer(string text)
    {
        var end = text.IndexOf(template.EndOfTurn, StringComparison.Ordinal);
        if (end >= 0) text = text[..end];
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return line.StartsWith("- ", StringComparison.Ordinal) ? line[2..].Trim() : line;
    }

    private static MemberCandidate? Match(IReadOnlyList<MemberCandidate> members, string answer)
    {
        if (answer.Length == 0) return null;
        var exact = members.FirstOrDefault(m => m.Display == answer);
        if (exact is not null) return exact;
        var name = MemberLocator.NameOf(answer);
        var byName = members.Where(m => MemberLocator.NameOf(m.Display) == name).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }
}
