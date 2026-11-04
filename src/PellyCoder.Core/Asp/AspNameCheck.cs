namespace PellyCoder.Core.Asp;

/// <summary>A name used in the patched page that no declaration, procedure or built-in explains.</summary>
public sealed record AspUnknownName(string Name, int Line);

/// <summary>
/// New unknown names; <see cref="Downgraded"/> means the check cannot be sure (missing includes, dynamic code)
/// and the caller shows the names as a warning instead of failing.
/// </summary>
public sealed record AspNameResult(IReadOnlyList<AspUnknownName> Unknown, bool Downgraded, string? DowngradeReason);

/// <summary>Name check of a patched page against the original (design §4.5.5).</summary>
public static class AspNameCheck
{
    private const string AdovbsFileName = "adovbs.inc";

    /// <summary>
    /// Names whose count of unknown uses grew beyond the original, as many as it grew, with the patched page's
    /// line numbers (the later occurrences of each name).
    /// </summary>
    public static AspNameResult NewUnknown(AspPage original, AspPage patched, AspIncludeSet includes, AspBuiltins builtins)
    {
        var baseline = UnknownUses(original, includes, builtins)
            .GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new List<AspUnknownName>();
        foreach (var group in UnknownUses(patched, includes, builtins).GroupBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
        {
            var extra = group.Count() - baseline.GetValueOrDefault(group.Key);
            if (extra > 0)
                result.AddRange(group.OrderBy(u => u.Line).TakeLast(extra).Select(u => new AspUnknownName(u.Name, u.Line)));
        }

        var missingIncludes = includes.Unresolved.Count > 0 || includes.Truncated;
        var reason = (missingIncludes, patched.UsesDynamicCode) switch
        {
            (true, true) => "include를 다 찾지 못했고 Execute·Eval이 있어 이름 검사를 경고로 낮췄습니다.",
            (true, false) => "include를 다 찾지 못해 이름 검사를 경고로 낮췄습니다.",
            (false, true) => "Execute·Eval이 있어 이름 검사를 경고로 낮췄습니다.",
            _ => null,
        };
        return new AspNameResult(result.OrderBy(u => u.Line).ToArray(), reason is not null, reason);
    }

    private static IEnumerable<AspNameUse> UnknownUses(AspPage page, AspIncludeSet includes, AspBuiltins builtins)
    {
        var pages = new List<AspPage> { page };
        pages.AddRange(includes.Files.Select(f => f.Page));
        var adoConstants = includes.Files.Any(f => IsAdovbs(f.Path))
            || pages.Any(p => p.Includes.Any(i => IsAdovbs(i.Path)) || p.Segments.Any(IsTypeLibMetadata));

        // Module-level declarations, every procedure and class name: visible from everywhere.
        var global = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Scoped declarations (locals, parameters, class fields) keyed by scope.
        var scoped = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pages)
        {
            foreach (var procedure in p.Procedures)
                global.Add(procedure.Name);
            foreach (var declaration in p.Declarations)
            {
                if (declaration.Procedure is null)
                    global.Add(declaration.Name);
                else
                {
                    if (!scoped.TryGetValue(declaration.Procedure, out var names))
                        scoped[declaration.Procedure] = names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    names.Add(declaration.Name);
                }
            }
            if (!page.OptionExplicit)
                foreach (var use in p.NameUses.Where(u => u.IsAssignment))
                    global.Add(use.Name);
        }

        // A Function (or Property Get) may assign its own name as the return value.
        var returnNames = page.Procedures
            .Where(p => p.Keyword is "Function" or "Property Get")
            .GroupBy(Scope, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);

        return page.NameUses.Where(use => !IsKnown(use));

        bool IsKnown(AspNameUse use)
        {
            if (builtins.IsKnown(use.Name, adoConstants) || global.Contains(use.Name))
                return true;
            if (use.Procedure is not { } scope)
                return false;
            if (InScope(scope, use.Name))
                return true;
            if (returnNames.TryGetValue(scope, out var returnName) && returnName.Equals(use.Name, StringComparison.OrdinalIgnoreCase))
                return true;
            // Inside a class method "C.M" the class fields (scope "C") are visible too.
            var dot = scope.IndexOf('.');
            return dot > 0 && InScope(scope[..dot], use.Name);
        }

        bool InScope(string scope, string name) => scoped.TryGetValue(scope, out var names) && names.Contains(name);
    }

    private static string Scope(AspProcedure procedure) =>
        procedure.ClassName is null ? procedure.Name : procedure.ClassName + "." + procedure.Name;

    private static bool IsAdovbs(string path)
    {
        var slash = path.LastIndexOfAny(['/', '\\']);
        return path[(slash + 1)..].Equals(AdovbsFileName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTypeLibMetadata(AspSegment segment) =>
        segment.Kind == AspSegmentKind.Metadata && segment.Text.Contains("typelib", StringComparison.OrdinalIgnoreCase);
}
