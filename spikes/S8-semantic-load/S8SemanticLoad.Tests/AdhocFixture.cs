using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

/// <summary>Builds in-memory solutions so metric tests run without MSBuild.</summary>
public static class AdhocFixture
{
    public static IReadOnlyList<MetadataReference> PlatformRefs { get; } =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(p) == "netstandard.dll" || Path.GetFileName(p) == "mscorlib.dll")
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .ToList();

    /// <summary>One project per tuple; <paramref name="projects"/> refs name earlier projects.</summary>
    public static Solution Build(params (string Project, string[] Refs, (string Path, string Code)[] Docs)[] projects) =>
        Build(null, projects);

    public static Solution Build(string? missingRef, params (string Project, string[] Refs, (string Path, string Code)[] Docs)[] projects)
    {
        var ws = new AdhocWorkspace();
        var solution = ws.CurrentSolution;
        var ids = new Dictionary<string, ProjectId>();
        foreach (var (name, refs, docs) in projects)
        {
            var id = ProjectId.CreateNewId(name);
            ids[name] = id;
            IEnumerable<MetadataReference> meta = PlatformRefs;
            if (missingRef is not null)
                meta = meta.Append(MetadataReference.CreateFromImage(File.ReadAllBytes(typeof(object).Assembly.Location), filePath: missingRef));
            solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Default, name, name, LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                metadataReferences: meta,
                projectReferences: refs.Select(r => new ProjectReference(ids[r]))));
            foreach (var (path, code) in docs)
                solution = solution.AddDocument(DocumentId.CreateNewId(id), Path.GetFileName(path), code, filePath: path);
        }
        return solution;
    }

    public static Solution Single(string code, string? missingRef = null) =>
        Build(missingRef, ("P", [], [("/fx/P/A.cs", code)]));
}
