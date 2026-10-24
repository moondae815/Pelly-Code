using S8SemanticLoad;

public class ProjectCensusTests
{
    [Fact] public void Census_reads_legacy_corpus_sln()
    {
        var c = ProjectCensusReader.Read(Path.Combine(TestPaths.Corpus, "Legacy", "Legacy.sln"));
        Assert.Equal((4, 0, 0), (c.LegacyCs, c.SdkCs, c.NonCs));
    }

    [Fact] public void Census_reads_modern_corpus_sln()
    {
        var c = ProjectCensusReader.Read(Path.Combine(TestPaths.Corpus, "Modern", "Modern.sln"));
        Assert.Equal((3, 0), (c.SdkCs, c.LegacyCs));
    }

    [Fact] public void Census_reads_slnx()
    {
        var c = ProjectCensusReader.Read(TestPaths.Fixture("census", "slnx", "App.slnx"));
        Assert.Equal((1, 1), (c.SdkCs, c.LegacyCs));
        Assert.All(c.Entries, e => Assert.True(File.Exists(e.Path), e.Path));
    }

    [Fact] public void Census_counts_non_csharp_separately()
    {
        var c = ProjectCensusReader.Read(TestPaths.Fixture("census", "mixed", "Mixed.sln"));
        Assert.Equal((1, 1, 1), (c.SdkCs, c.NonCs, c.CSharpTotal));
    }

    [Fact] public void Census_handles_spaces_and_korean_in_paths()
    {
        var c = ProjectCensusReader.Read(TestPaths.Fixture("census", "한글 경로", "Space.sln"));
        Assert.Equal(1, c.SdkCs);
        Assert.True(File.Exists(c.Entries.Single().Path));
    }

    [Fact] public void Census_marks_unrestored_sdk()
    {
        Assert.Equal(1, ProjectCensusReader.Read(TestPaths.Fixture("census", "mixed", "Mixed.sln")).UnrestoredSdk);
        Assert.Equal(0, ProjectCensusReader.Read(TestPaths.Fixture("census", "restored", "Restored.sln")).UnrestoredSdk);
    }

    // 최종 리뷰 I4: 웹 사이트 프로젝트는 폴더 경로나 URL로 들어온다. C# 아님으로 세고 예외 없이 읽어야 한다.
    [Fact] public void Census_reads_web_site_entries_as_non_csharp()
    {
        var c = ProjectCensusReader.Read(TestPaths.Fixture("census", "website", "Web.sln"));
        Assert.Equal((1, 2), (c.SdkCs, c.NonCs));
        Assert.Contains(c.Entries, e => e.Path == "http://localhost/Web2");
    }

    [Fact] public void Census_accepts_single_csproj()
    {
        var c = ProjectCensusReader.Read(Path.Combine(TestPaths.Corpus, "Modern", "Shop.Api", "Shop.Api.csproj"));
        Assert.Single(c.Entries);
        Assert.Equal(1, c.SdkCs);
    }
}
