using System.IO.Abstractions;
using System.Text;
using System.Xml;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs.Drafting;

/// <param name="Scopes">The <c>--scope</c> paths as typed (resolved against the current directory).</param>
public sealed record DraftRequest(string Id, string Request, IReadOnlyList<string> Scopes);

/// <summary>A task line the impact search added: <paramref name="FromTaskId"/> is the seed whose change it follows.</summary>
public sealed record AddedImpactTask(string TaskId, string Path, string Member, string FromTaskId);

/// <param name="Document">The full <c>changes/&lt;id&gt;.md</c> text (<c>\n</c>, no BOM); nothing is written there.</param>
/// <param name="DroppedLines">Task-shaped lines of the answer that failed the grammar, verbatim.</param>
/// <param name="Check">The final check of <paramref name="Document"/>; its line numbers point into it.</param>
/// <param name="ImpactSkippedForErrors">The static check had errors, so no impact search ran.</param>
/// <param name="ImpactSkippedForCap">
/// How many impact task lines were left out because the model's tasks plus all of them would pass
/// <see cref="ChangeSpecParser.MaxTasks"/>; 0 when none were left out for that reason.
/// </param>
public sealed record DraftOutcome(
    string Document,
    IReadOnlyList<string> DroppedLines,
    IReadOnlyList<AddedImpactTask> Added,
    SpecCheckResult Check,
    bool ImpactSkippedForErrors,
    DraftCall Call,
    DraftIndex Index,
    int ImpactSkippedForCap = 0);

/// <param name="ExitCode">4 for scope, budget or environment problems; 1 when the draft has no task.</param>
public sealed record DraftFailure(int ExitCode, string Message);

/// <summary>
/// <c>pelly spec draft</c> without the console (design §4.3–§4.4): index the scope, ask the model, keep the answer's grammar
/// lines, build the spec document, check it in <c>.pelly/drafts/&lt;id&gt;.md</c> (removed afterwards) and add the semantic
/// impact targets of grouped tasks as task lines. Never writes under <c>changes/</c>.
/// </summary>
public sealed class SpecDrafter(IFileSystem fs, DraftModel model, IImpactFinder impact, SqlImpactSetup? sql, int sqlCompatLevel)
{
    public const int TitleLength = 60;
    private const int BiggestFilesShown = 5;
    private const string AcTemplate = "- AC1: 이 변경이 끝났을 때 참이어야 하는 것";

    /// <summary>The reasons of a semantic C# target that sit inside a member the target names (not 「멤버 밖」, not SQL or string ones).</summary>
    private static readonly HashSet<string> MemberReasons = new(StringComparer.Ordinal) { "구현", "재정의", "기반 멤버", "호출" };

    public async Task<(DraftOutcome? Outcome, DraftFailure? Failure)> DraftAsync(
        Workspace ws, DraftRequest request, CancellationToken ct, Action<TimeSpan>? onWaiting = null)
    {
        var (index, scopeError) = DraftIndexBuilder.Build(fs, ws, request.Scopes, sqlCompatLevel);
        if (scopeError is not null) return Fail(4, scopeError.Message);
        if (index!.Files.Count == 0) return Fail(4, NoSources(request.Scopes, index.Skipped));
        var rendered = index.Render();

        var inputTokens = model.CountInput(request.Request, rendered);
        if (inputTokens > DraftPrompt.InputBudget) return Fail(4, OverBudget(index, inputTokens));

        // Checked before the model runs, so an unusable workspace never costs a model call (WorkspaceLocator needs the folder).
        var draftDir = fs.Path.Combine(ws.PellyDir, "drafts");
        var draftPath = fs.Path.Combine(draftDir, request.Id + ".md");
        try { fs.Directory.CreateDirectory(draftDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(4, $"초안 검사 폴더를 만들 수 없습니다: {draftDir} ({ex.Message})");
        }

        // The checker finds the workspace from the file's path; without a .git, .svn or *.sln above it, that would be .pelly/drafts.
        if (!SamePath(WorkspaceLocator.Resolve(fs, draftPath).Root, ws.Root))
            return Fail(4, $"작업 공간 루트({ws.Root})에 .git, .svn 또는 *.sln이 없어 초안을 검사할 수 없습니다.");

        var call = await model.RunAsync(request.Request, rendered, ct, onWaiting);

        var (taskLines, dropped) = ReadAnswer(call.Answer);
        if (taskLines.Count == 0)
        {
            var message = "모델의 답에 형식에 맞는 작업 줄이 없어 쓸 것이 없습니다.";
            if (dropped.Count > 0) message += "\n형식에 맞지 않아 버린 줄:\n" + string.Join("\n", dropped);
            return Fail(1, message);
        }

        try
        {
            var document = Compose(request, taskLines);
            if (Write(draftPath, document) is { } writeError) return Fail(4, writeError);

            var first = SpecChecker.Check(fs, draftPath, sqlCompatLevel);
            var skipped = first.HasErrors;
            var csChecked = await SpecChecker.AddImpactAsync(first, impact, impactAllCsTasks: false, ct);
            // The SQL search picks its database like spec check --impact (design §4.4): production when configured, else development.
            var sqlRequest = sql is null || skipped
                ? null
                : SpecChecker.SqlRequest(fs, sql, allowDev: true, SpecChecker.SpecCheckListPath(fs, first, draftPath));
            var searched = sqlRequest is null ? csChecked : await SpecChecker.AddImpactAsync(csChecked, null, false, ct, sqlRequest);

            var (lines, added, skippedForCap) = AddImpactTasks(ws, first.Spec, taskLines, searched.Impact);
            var check = searched;
            if (added.Count > 0)
            {
                document = Compose(request, lines);
                if (Write(draftPath, document) is { } rewriteError) return Fail(4, rewriteError);
                check = Recheck(draftPath, searched, csChecked.Issues.Count);
            }

            if (check.HasErrors) (document, check) = WithCheckComment(document, check);
            return (new DraftOutcome(document, dropped, added, check, skipped, call, index, skippedForCap), null);
        }
        finally
        {
            try { if (fs.File.Exists(draftPath)) fs.File.Delete(draftPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover draft is overwritten next time */ }
        }
    }

    private static (DraftOutcome?, DraftFailure?) Fail(int code, string message) => (null, new DraftFailure(code, message));

    private static string NoSources(IReadOnlyList<string> scopes, IReadOnlyList<string> skipped)
    {
        var message = "범위에 .cs·.sql 파일이 없습니다: " + string.Join(", ", scopes);
        if (skipped.Count > 0) message += "\n읽지 못해 색인에서 뺀 파일:\n" + string.Join("\n", skipped.Select(p => "  " + p));
        return message;
    }

    private string OverBudget(DraftIndex index, int tokens)
    {
        var biggest = index.Files
            .Select(f => (f.Path, Tokens: QwenTokenCounter.Shared.Count(new DraftIndex([f], []).Render())))
            .OrderByDescending(f => f.Tokens)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Take(BiggestFilesShown)
            .Select(f => $"  {f.Path} ({f.Tokens}토큰)");
        return $"첫 입력이 {tokens}토큰으로 상한 {DraftPrompt.InputBudget}토큰을 넘어 모델을 부르지 않았습니다. 색인이 큰 파일:\n"
            + string.Join("\n", biggest) + "\n--scope를 좁히십시오.";
    }

    /// <summary>
    /// Splits the answer into grammar task lines and dropped ones. A line starting with <c>- [</c> is task-shaped: kept when the
    /// product parser takes it as a task, else dropped verbatim. Any other line (prose, code fences) is ignored.
    /// </summary>
    internal static (List<string> Tasks, List<string> Dropped) ReadAnswer(string answer)
    {
        var tasks = new List<string>();
        var dropped = new List<string>();
        foreach (var raw in answer.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (!line.TrimStart().StartsWith("- [", StringComparison.Ordinal)) continue;
            if (ChangeSpecParser.Parse("## 작업\n" + line).Tasks.Count == 1) tasks.Add(line);
            else dropped.Add(line);
        }
        return (tasks, dropped);
    }

    internal static string Compose(DraftRequest request, IEnumerable<string> taskLines)
    {
        var goal = request.Request.Replace("\r\n", "\n").TrimEnd('\n');
        var sb = new StringBuilder();
        sb.Append("---\nid: ").Append(request.Id).Append("\nstatus: draft\n---\n");
        sb.Append("# ").Append(Title(goal, request.Id)).Append("\n\n");
        sb.Append("## 목표\n").Append(goal).Append("\n\n");
        sb.Append("## 수용 기준\n").Append(AcTemplate).Append("\n\n");
        sb.Append("## 작업\n");
        foreach (var line in taskLines) sb.Append(line).Append('\n');
        return sb.ToString();
    }

    private static string Title(string goal, string id)
    {
        var first = goal.Split('\n')[0].Trim();
        if (first.Length == 0) return id;
        if (first.Length <= TitleLength) return first;
        var cut = char.IsHighSurrogate(first[TitleLength - 1]) ? TitleLength - 1 : TitleLength;
        return first[..cut].TrimEnd();
    }

    private string? Write(string path, string document)
    {
        try
        {
            fs.File.WriteAllText(path, document, new UTF8Encoding(false));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"초안 검사 파일을 쓸 수 없습니다: {path} ({ex.Message})";
        }
    }

    /// <summary>
    /// Adds a task line for each semantic C# target that names a member (design §4.4): inside a member, the only member of that
    /// name in its file, reached only from seeds of one group and not already a task. It goes after the group's last task,
    /// numbered from the highest task number + 1 in the order the targets came. When the model's tasks and all of these lines
    /// together would pass <see cref="ChangeSpecParser.MaxTasks"/>, none is added (a partial set would look complete) and
    /// <c>SkippedForCap</c> says how many there were.
    /// </summary>
    private (List<string> Lines, List<AddedImpactTask> Added, int SkippedForCap) AddImpactTasks(
        Workspace ws, ChangeSpec spec, List<string> taskLines, ImpactResult? result)
    {
        var added = new List<AddedImpactTask>();
        if (result is not { Mode: ImpactMode.Semantic } || spec.Tasks.Count != taskLines.Count) return (taskLines, added, 0);

        var lines = spec.Tasks.Select((t, i) => (Text: taskLines[i], t.Group)).ToList();
        var groupOf = spec.Tasks.GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Group, StringComparer.Ordinal);
        var next = spec.Tasks.Select(t => int.TryParse(t.Id.AsSpan(1), out var n) ? n : 0).DefaultIfEmpty(0).Max() + 1;

        var places = result.Targets
            .Where(t => t.TargetKind == ImpactTargetKind.CSharpMember && MemberReasons.Contains(t.Reason) && t.Path.Length > 0)
            .GroupBy(t => (Path: t.Path.Replace('\\', '/'), t.Member), PlaceComparer.Instance);
        foreach (var place in places)
        {
            var target = place.First();
            if (spec.Tasks.Any(task => ImpactCheck.Covers(task, target))) continue;
            var groups = place.Select(t => groupOf.GetValueOrDefault(t.SeedTaskId)).Distinct().ToList();
            if (groups is not [{ } group]) continue;
            if (!IsUniqueMember(ws, place.Key.Path, place.Key.Member)) continue;

            var id = "T" + next++;
            var line = $"- [ ] {id} cs {place.Key.Path}#{place.Key.Member} {target.SeedTaskId}의 변경에 맞춰 이 코드를 고친다 (영향 탐색으로 추가) [{group}]";
            lines.Insert(lines.FindLastIndex(l => l.Group == group) + 1, (line, group));
            added.Add(new AddedImpactTask(id, place.Key.Path, place.Key.Member, target.SeedTaskId));
        }
        if (spec.Tasks.Count + added.Count > ChangeSpecParser.MaxTasks) return (taskLines, [], added.Count);
        return (lines.Select(l => l.Text).ToList(), added, 0);
    }

    /// <summary>True when the file has exactly one method or property of that name (a constructor's name cannot be told from its type's).</summary>
    private bool IsUniqueMember(Workspace ws, string path, string member)
    {
        var full = fs.Path.GetFullPath(fs.Path.Combine(ws.Root, path));
        try
        {
            if (!fs.File.Exists(full)) return false;
            var text = TextFileCodec.Read(fs, full).Text;
            var version = LanguageVersionDetector.Detect(fs, ProjectFileLocator.FindNearestCsproj(fs, full)).Version;
            var found = MemberLocator.FindByName(text, version, null, member);
            return found is [var only] && only.ContainingType.Split('.')[^1] != member;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// The static check of the document with the added lines, plus the impact warnings of the one search that ran: the C# ones
    /// recomputed against the new tasks (an added target is no longer missing), the SQL ones carried with their task lines moved.
    /// </summary>
    private SpecCheckResult Recheck(string draftPath, SpecCheckResult searched, int csIssueEnd)
    {
        var again = SpecChecker.Check(fs, draftPath, sqlCompatLevel);
        var cs = ImpactCheck.Issues(again.Spec, ImpactCheck.Seeds(again.Spec, allCsTasks: false), searched.Impact!);
        var newLine = again.Spec.Tasks.ToDictionary(t => t.Id, t => t.Line, StringComparer.Ordinal);
        var moved = searched.Spec.Tasks
            .Where(t => newLine.ContainsKey(t.Id))
            .ToDictionary(t => t.Line, t => newLine[t.Id]);
        var sqlIssues = searched.Issues.Skip(csIssueEnd).Select(i => i with { Line = moved.GetValueOrDefault(i.Line, i.Line) });
        return again with
        {
            Issues = [.. again.Issues, .. cs, .. sqlIssues],
            Impact = searched.Impact,
            SqlImpact = searched.SqlImpact,
            SqlImpactSummary = searched.SqlImpactSummary,
        };
    }

    /// <summary>
    /// Puts the check result as an HTML comment right under <c>## 작업</c> (design §4.3 step 6) and moves the issue lines below it
    /// so they still point into the returned document.
    /// </summary>
    private static (string Document, SpecCheckResult Check) WithCheckComment(string document, SpecCheckResult check)
    {
        var errors = check.Issues.Count(i => i.Severity == SpecIssueSeverity.Error);
        var comment = new List<string> { $"<!-- spec check: 오류 {errors}, 경고 {check.Issues.Count - errors}" };
        comment.AddRange(check.Issues.Select(i =>
            $"  {(i.Severity == SpecIssueSeverity.Error ? "오류" : "경고")} {i.Code}: {i.Message.Replace("-->", "-- >").Replace('\n', ' ')}"));
        comment.Add("-->");

        var lines = document.Split('\n').ToList();
        var heading = lines.IndexOf("## 작업"); // 0-based; the comment goes on 1-based line heading + 2 onwards
        lines.InsertRange(heading + 1, comment);
        var shifted = check.Issues.Select(i => i.Line > heading + 1 ? i with { Line = i.Line + comment.Count } : i).ToList();
        return (string.Join('\n', lines), check with { Issues = shifted });
    }

    private bool SamePath(string a, string b) =>
        PathComparers.ForOs.Equals(fs.Path.TrimEndingDirectorySeparator(fs.Path.GetFullPath(a)), fs.Path.TrimEndingDirectorySeparator(fs.Path.GetFullPath(b)));

    private sealed class PlaceComparer : IEqualityComparer<(string Path, string Member)>
    {
        public static readonly PlaceComparer Instance = new();

        public bool Equals((string Path, string Member) x, (string Path, string Member) y) =>
            PathComparers.ForOs.Equals(x.Path, y.Path) && string.Equals(x.Member, y.Member, StringComparison.Ordinal);

        public int GetHashCode((string Path, string Member) obj) =>
            HashCode.Combine(PathComparers.ForOs.GetHashCode(obj.Path), StringComparer.Ordinal.GetHashCode(obj.Member));
    }
}
