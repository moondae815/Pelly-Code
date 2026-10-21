using PellyCoder.Core.Llm;
using S6Planning;

public class PlanPromptTests
{
    [Fact] public void All_twelve_cases_fit_input_budget()
    {
        var t = QwenChatTemplate.LoadDefault();
        foreach (var c in GoldLoader.LoadAll(TestPaths.Cases))
        {
            var idx = CorpusIndexBuilder.Build(TestPaths.Corpus, c.Scope);
            Assert.True(PlanPrompt.CountTokens(PlanPrompt.Raw(t, c.Request, idx, think: true)) <= 4096, c.Id);
        }
    }

    [Fact] public void Example_names_do_not_collide_with_corpus()
    {
        var all = CorpusIndexBuilder.Build(TestPaths.Corpus, new[] { "Modern", "Legacy", "Sql" }).Render();
        Assert.All(PlanPrompt.ExampleNames, n => Assert.DoesNotContain(n, all));
    }

    [Fact] public void Example_names_appear_in_system_prompt() =>
        Assert.All(PlanPrompt.ExampleNames, n => Assert.Contains(n, PlanPrompt.System));

    [Fact] public void Example_in_system_prompt_obeys_the_grammar()
    {
        var p = PlanParser.Parse(PlanPrompt.System);
        Assert.Equal(4, p.Tasks.Count);
        // The only non-conforming checkbox line is the grammar template itself.
        Assert.Equal("- [ ] T<n> <cs|sql> <path>[#Member] <what to change> [G<k>]", Assert.Single(p.InvalidLines));
    }

    [Fact] public void Raw_prefill_differs_by_arm()
    {
        var t = QwenChatTemplate.LoadDefault();
        var idx = new CorpusIndex(Array.Empty<IndexFile>());
        Assert.EndsWith(t.EmptyThink, PlanPrompt.Raw(t, "r", idx, think: false));
        Assert.EndsWith(t.ThinkOpen, PlanPrompt.Raw(t, "r", idx, think: true));
    }

    [Fact] public void User_message_carries_index_then_request()
    {
        var u = PlanPrompt.User("요청문", new CorpusIndex(new[] { new IndexFile("X/A.cs", "cs", Array.Empty<IndexMember>()) }));
        Assert.True(u.IndexOf("## X/A.cs", StringComparison.Ordinal) < u.IndexOf("요청문", StringComparison.Ordinal));
        Assert.StartsWith("Index:\n", u);
    }
}
