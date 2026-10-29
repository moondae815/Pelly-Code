using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Reporting;
using PellyCoder.Eval;
using PellyCoder.Integration.Tests.Builds;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.E2E;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.CharTests;

/// <summary>
/// <c>pelly test</c> against restored copies of the corpus with canned model answers: real builds, real test runs, a real
/// mutation of the target. The answers are the continuation of the prefilled <c>### USINGS</c> line.
/// </summary>
public sealed class CharTestIntegrationTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Clamps (Page 0 → 1, PageSize 500 → 100) and fills To from From + 30 days, trims the keyword. Both fail when
    /// <c>SearchAsync</c> throws.</summary>
    private const string GoodAnswer = """
        using Shop.Api.Models;
        ### MEMBERS
        [Fact]
        public async Task SearchAsync_PageSizeOverLimit_ClampsPaging()
        {
            var repo = new FakeOrderRepository(NewOrder(OrderStatus.Paid));
            var service = new OrderService(repo, NullLogger<OrderService>.Instance);
            var request = new OrderSearchRequest { Page = 0, PageSize = 500 };

            var result = await service.SearchAsync(request);

            Assert.Equal(1, request.Page);
            Assert.Equal(100, request.PageSize);
            Assert.Single(result);
        }

        [Fact]
        public async Task SearchAsync_OnlyFrom_FillsToAndTrimsKeyword()
        {
            var repo = new FakeOrderRepository(NewOrder(OrderStatus.Paid));
            var service = new OrderService(repo, NullLogger<OrderService>.Instance);
            var request = new OrderSearchRequest { From = new DateTime(2026, 10, 1), Keyword = "  머그  " };

            await service.SearchAsync(request);

            Assert.Equal(new DateTime(2026, 10, 31), request.To);
            Assert.Equal("머그", request.Keyword);
        }
        """;

    /// <summary>Calls the target without awaiting it and asserts only on the service: passes on current code and under the
    /// mutation (the faulted task is never observed).</summary>
    private const string UselessAnswer = """
        ### MEMBERS
        [Fact]
        public void SearchAsync_Runs()
        {
            var service = new OrderService(new FakeOrderRepository(NewOrder(OrderStatus.Paid)), NullLogger<OrderService>.Instance);

            _ = service.SearchAsync(new OrderSearchRequest());

            Assert.NotNull(service);
        }
        """;

    private const string WrongExpectedAnswer = """
        ### MEMBERS
        [Fact]
        public async Task SearchAsync_PageSizeOverLimit_Clamps()
        {
            var service = new OrderService(new FakeOrderRepository(NewOrder(OrderStatus.Paid)), NullLogger<OrderService>.Instance);
            var request = new OrderSearchRequest { PageSize = 500 };

            await service.SearchAsync(request);

            Assert.Equal(50, request.PageSize);
        }
        """;

    private static readonly string ActualAnswer = WrongExpectedAnswer.Replace("Assert.Equal(50,", "Assert.Equal(100,", StringComparison.Ordinal);

    /// <summary>Legacy <c>OrderDao.GetOrder</c> (Shop.Data, no OrderDaoTests class): an unsupported connection-string keyword makes
    /// <c>new SqlConnection</c> throw <c>ArgumentException</c> before any database is reached.</summary>
    private const string LegacyAnswer = """
        using System;
        using Shop.Data;
        ### MEMBERS
        [TestMethod]
        public void GetOrder_UnsupportedConnectionKeyword_ThrowsArgumentException()
        {
            var dao = new OrderDao("Foo=Bar");

            Assert.ThrowsException<ArgumentException>(() => dao.GetOrder(1));
        }
        """;

    /// <summary>A new <c>OrdersControllerTests</c> file. Its only <c>using</c> is the framework one the skeleton leaves out (not declared in
    /// the workspace): <c>Task</c>, <c>List</c>, <c>OrdersController</c>, <c>IOrderService</c> and <c>Order</c> resolve only because the
    /// skeleton carries the target's workspace and core BCL usings and its namespace.</summary>
    private const string ControllerAnswer = """
        using Microsoft.AspNetCore.Mvc;
        ### MEMBERS
        [Fact]
        public async Task Get_MatchingOrder_ReturnsOk()
        {
            var order = new Order { OrderId = 7 };
            var sut = new OrdersController(new FakeOrderService(new List<Order> { order }));

            var result = await sut.Get(7);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Same(order, ok.Value);
        }

        [Fact]
        public async Task Get_NoMatch_ReturnsNotFound()
        {
            var sut = new OrdersController(new FakeOrderService(new List<Order>()));

            var result = await sut.Get(7);

            Assert.IsType<NotFoundResult>(result);
        }

        private sealed class FakeOrderService : IOrderService
        {
            private readonly IReadOnlyList<Order> _orders;
            public FakeOrderService(IReadOnlyList<Order> orders) { _orders = orders; }
            public Task CancelAsync(int orderId, string reason) => throw new NotImplementedException();
            public Task<decimal> CalculateRefundAsync(int orderId) => throw new NotImplementedException();
            public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request) => Task.FromResult(_orders);
        }
        """;

    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var dir in _temps)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* temp folder, best effort */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Modern_GoodTests_Kept_TargetBytesUnchanged()
    {
        var root = await PrepareModernAsync();
        var target = OrderServiceFile(root);
        var before = File.ReadAllBytes(target);

        var (exit, ui, requests) = await RunAsync(target, "SearchAsync", [GoodAnswer]);
        var run = DumpRun(root, ui);
        Assert.Equal(1, requests);

        Assert.Equal(ExitCodes.Success, exit);
        var tests = File.ReadAllText(TestFile(root));
        Assert.Contains("SearchAsync_PageSizeOverLimit_ClampsPaging", tests, StringComparison.Ordinal);
        Assert.Contains("SearchAsync_OnlyFrom_FillsToAndTrimsKeyword", tests, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(target));
        AssertNoMarker(run);
        var charTest = ReadSummary(run).GetProperty("charTest");
        Assert.Equal(2, charTest.GetProperty("kept").GetInt32());
        Assert.Equal(2, charTest.GetProperty("killedByMutation").GetInt32());
    }

    [Fact]
    public async Task Modern_AssertsNothingUseful_SurvivesMutation_Exit1()
    {
        var root = await PrepareModernAsync();
        var target = OrderServiceFile(root);
        var before = File.ReadAllBytes(target);
        var testsBefore = File.ReadAllBytes(TestFile(root));

        var (exit, ui, requests) = await RunAsync(target, "SearchAsync", [UselessAnswer, UselessAnswer, UselessAnswer, UselessAnswer]);
        var run = DumpRun(root, ui);
        Assert.Equal(4, requests);

        Assert.Equal(ExitCodes.VerifyFailed, exit);
        var summary = ReadSummary(run);
        Assert.Equal(FailureKinds.CharTestSurvivesMutation, summary.GetProperty("failureKind").GetString());
        Assert.Equal(4, summary.GetProperty("attempts").GetInt32());
        Assert.Equal(testsBefore, File.ReadAllBytes(TestFile(root)));
        Assert.Equal(before, File.ReadAllBytes(target));
        AssertNoMarker(run);
    }

    [Fact]
    public async Task Modern_WrongExpected_ThenActual_Exit0()
    {
        var root = await PrepareModernAsync();
        var target = OrderServiceFile(root);
        var before = File.ReadAllBytes(target);

        var (exit, ui, requests) = await RunAsync(target, "SearchAsync", [WrongExpectedAnswer, ActualAnswer]);
        var run = DumpRun(root, ui);
        Assert.Equal(2, requests);

        Assert.Equal(ExitCodes.Success, exit);
        var prompt = File.ReadAllText(Path.Combine(run, "attempt-2", "prompt.txt"));
        var at = prompt.IndexOf("### FEEDBACK", StringComparison.Ordinal);
        Assert.True(at >= 0, "attempt 2 prompt has no FEEDBACK");
        var feedback = prompt[at..];
        Assert.Contains("TESTS FAIL ON CURRENT CODE", feedback, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"Actual:\s+100\b"), feedback);
        Assert.Contains("Assert.Equal(100,", File.ReadAllText(TestFile(root)), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(target));
        AssertNoMarker(run);
    }

    [Fact]
    public async Task Modern_NewFile_SkeletonUsings_Kept()
    {
        var root = await PrepareModernAsync();
        var target = Path.Combine(root, "Shop.Api", "Controllers", "OrdersController.cs");
        var newFile = Path.Combine(root, "Shop.Api.Tests", "OrdersControllerTests.cs");
        Assert.False(File.Exists(newFile));
        var before = File.ReadAllBytes(target);

        var (exit, ui, requests) = await RunAsync(target, "Get", [ControllerAnswer]);
        var run = DumpRun(root, ui);
        Assert.Equal(1, requests);

        Assert.Equal(ExitCodes.Success, exit);
        var tests = File.ReadAllText(newFile);
        Assert.Contains("using Shop.Api.Controllers;", tests, StringComparison.Ordinal);
        Assert.Contains("using Shop.Api.Services;", tests, StringComparison.Ordinal);
        Assert.Contains("using Shop.Api.Models;", tests, StringComparison.Ordinal);
        Assert.Contains("using System.Threading.Tasks;", tests, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(tests, "using Microsoft.AspNetCore.Mvc;"));
        Assert.Contains("Get_NoMatch_ReturnsNotFound", tests, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(target));
        AssertNoMarker(run);
        var charTest = ReadSummary(run).GetProperty("charTest");
        Assert.True(charTest.GetProperty("newFile").GetBoolean());
        Assert.Equal(2, charTest.GetProperty("killedByMutation").GetInt32());
    }

    // Windows 미확인(실기): CI windows-integration 에서만 실행된다 (MSBuild, vstest, Legacy/packages).
    [WindowsFact]
    public async Task Legacy_MsTest_NewFile_Kept()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.True(Directory.Exists(CorpusPaths.Get("Legacy/packages")),
            "eval/corpus/Legacy/packages 가 없습니다. 먼저 eval/corpus/Legacy/build.ps1 을 실행하세요 (CI는 Windows tests 앞에서 실행한다).");
        // The same copy as LegacyBuildWindowsTests: everything (packages included) but bin, obj and TestResults.
        var root = CorpusCopy.Create("Legacy", includeBuildOutput: false);
        _temps.Add(root);

        var target = Path.Combine(root, "Shop.Data", "OrderDao.cs");
        var csproj = Path.Combine(root, "Shop.Tests", "Shop.Tests.csproj");
        var newFile = Path.Combine(root, "Shop.Tests", "OrderDaoTests.cs");
        Assert.False(File.Exists(newFile));
        var before = File.ReadAllBytes(target);

        var adapter = Path.Combine(root, "packages", "MSTest.TestAdapter.2.2.10", "build", "_common");
        var command = await VstestCommand.ResolveAsync(
            $"vstest.console.exe {{dll}} \"/TestAdapterPath:{adapter}\" /logger:trx;LogFileName={{trx}}",
            new SystemProcessRunner(TimeProvider.System), new FileSystem(), ct);
        var (exit, ui, requests) = await RunAsync(target, "GetOrder", [LegacyAnswer], new Dictionary<string, string?> { ["test:command"] = command });
        var run = DumpRun(root, ui);
        Assert.Equal(1, requests);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("GetOrder_UnsupportedConnectionKeyword_ThrowsArgumentException", File.ReadAllText(newFile), StringComparison.Ordinal);
        Assert.Contains("<Compile Include=\"OrderDaoTests.cs\" />", File.ReadAllText(csproj), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(target));
        AssertNoMarker(run);
        var charTest = ReadSummary(run).GetProperty("charTest");
        Assert.True(charTest.GetProperty("newFile").GetBoolean());
        Assert.Equal(1, charTest.GetProperty("kept").GetInt32());
    }

    // ---- plumbing ----

    private static string OrderServiceFile(string root) => Path.Combine(root, "Shop.Api", "Services", "OrderService.cs");

    private static string TestFile(string root) => Path.Combine(root, "Shop.Api.Tests", "OrderServiceTests.cs");

    private static void AssertNoMarker(string run) =>
        Assert.False(File.Exists(Path.Combine(run, CharTestPending.FileName)), "pending marker left in " + run);

    private static JsonElement ReadSummary(string run) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "summary.json"))).RootElement.Clone();

    /// <summary>A restored copy of the Modern corpus under a link-free root (the same steps as <c>M3aEndToEndTests</c>).</summary>
    private async Task<string> PrepareModernAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "pelly-chartest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var root = CsEndToEndTests.RealPath(temp);
        _temps.Add(root);
        CsEndToEndTests.CopyDirectory(CorpusPaths.Get("Modern"), root);
        Assert.False(File.Exists(Path.Combine(root, "global.json")));
        var restore = await new SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec("dotnet", ["restore", "Modern.sln", "--nologo", "--disable-parallel"], root, TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);
        Assert.True(restore.ExitCode == 0, "dotnet restore 실패:\n" + restore.StdOut + restore.StdErr);
        return root;
    }

    private static async Task<(int Exit, RecordingUserInteraction Ui, int Requests)> RunAsync(
        string file, string method, IReadOnlyList<string> answers, Dictionary<string, string?>? overrides = null)
    {
        var fs = new FileSystem();
        // One canned answer per attempt: retries must not first ask for a think block.
        var settings = new Dictionary<string, string?>(overrides ?? []) { ["llm:thinkMode"] = "off" };
        var loaded = PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), settings);
        var options = Assert.IsType<PellyOptions>(loaded.Options);
        var backend = new CannedLlmBackend(answers);
        var ui = new RecordingUserInteraction();
        var orchestrator = FsmOrchestratorFactory.CreateCharTest(fs, backend, ui, options, TimeProvider.System, new SystemProcessRunner(TimeProvider.System));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var exit = await orchestrator.RunAsync(
            new CharTestRequest(file, null, method, null, null, CharTestLimits.DefaultCount, null, Yes: true, DryRun: false, MaxRetries: null, Verbose: false),
            cts.Token);
        return (exit, ui, backend.Requests.Count);
    }

    /// <summary>Writes the run's lines and summary to the test output and returns the newest run folder (kept under
    /// <c>PELLY_TEST_KEEP</c> when set).</summary>
    private string DumpRun(string root, RecordingUserInteraction ui, [CallerMemberName] string test = "")
    {
        foreach (var line in ui.Lines) output.WriteLine(line);
        var runs = Path.Combine(root, ".pelly", "runs");
        Assert.True(Directory.Exists(runs), "no run folder: " + runs);
        var run = Directory.GetDirectories(runs).Order(StringComparer.Ordinal).Last();
        var summary = Path.Combine(run, "summary.json");
        if (File.Exists(summary)) output.WriteLine(File.ReadAllText(summary));
        var keep = Environment.GetEnvironmentVariable("PELLY_TEST_KEEP");
        if (!string.IsNullOrEmpty(keep))
        {
            var dest = Path.Combine(keep, test + "-" + Path.GetFileName(root), Path.GetFileName(run));
            CsEndToEndTests.CopyDirectory(run, dest);
            File.WriteAllLines(Path.Combine(dest, "ui-lines.txt"), ui.Lines);
            output.WriteLine($"run kept at {dest}");
        }
        return run;
    }
}
