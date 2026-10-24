using S8SemanticLoad;

public class ReferenceProbeTests
{
    [Fact] public async Task Probe_picks_deterministic_methods_and_times_them()
    {
        var methods = string.Join(" ", Enumerable.Range(0, 25).Select(i => $"public void M{i}(){{ {(i % 2 == 0 ? $"M{i + 1}();" : "")} }}"));
        var s = AdhocFixture.Single($"public class A {{ {methods} }}");
        var a = await ReferenceProbe.RunAsync(s);
        var b = await ReferenceProbe.RunAsync(s);
        Assert.Equal(20, a.Methods);
        Assert.Equal(a.ZeroReferenceRatio, b.ZeroReferenceRatio);
        Assert.True(a.MaxSeconds >= a.MeanSeconds);
    }

    [Fact] public async Task Probe_counts_zero_reference_methods()
    {
        var r = await ReferenceProbe.RunAsync(AdhocFixture.Single("public class A { public void Used(){} public void Unused(){} void M(){ Used(); } }"));
        Assert.Equal(2, r.Methods);
        Assert.Equal(0.5, r.ZeroReferenceRatio);
    }
}
