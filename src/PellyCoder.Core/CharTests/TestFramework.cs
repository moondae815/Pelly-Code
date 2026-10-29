using System.IO.Abstractions;
using System.Xml.Linq;

namespace PellyCoder.Core.CharTests;

public enum TestFramework { XunitV2, XunitV3, MsTest, NUnit }

/// <summary>Mocking and assertion packages the test project references (they decide what the static checks allow and count).</summary>
[Flags]
public enum TestLibraries
{
    None = 0,
    Moq = 1,
    NSubstitute = 2,
    FakeItEasy = 4,
    FluentAssertions = 8,
    Shouldly = 16,
}

/// <summary>Per-framework facts used to prompt for, check and count tests. <c>DataTestAttributes</c> are the
/// data-driven attributes (Theory, TestCase, ...) that count as test methods besides <c>TestAttribute</c>.
/// <c>AssertEqualFormat</c> is the equality assertion with <c>{0}</c> = expected and <c>{1}</c> = actual.</summary>
public sealed record FrameworkInfo(
    TestFramework Framework,
    string TestAttribute,
    string? ClassAttribute,
    IReadOnlyList<string> Usings,
    IReadOnlyList<string> SkipMarkers,
    IReadOnlyList<string> ThrowsAssertions,
    string ExampleTest,
    IReadOnlyList<string> DataTestAttributes,
    string AssertEqualFormat)
{
    public string AssertEqual(string expected, string actual) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, AssertEqualFormat, expected, actual);
}

public static class TestFrameworks
{
    private static readonly FrameworkInfo XunitV2 = Xunit(TestFramework.XunitV2);
    private static readonly FrameworkInfo XunitV3 = Xunit(TestFramework.XunitV3);

    private static readonly FrameworkInfo MsTest = new(
        TestFramework.MsTest, "TestMethod", "TestClass",
        ["Microsoft.VisualStudio.TestTools.UnitTesting"],
        ["Ignore", "Assert.Inconclusive"],
        ["Assert.ThrowsException", "Assert.ThrowsExceptionAsync", "ExpectedException"],
        """
        [TestMethod]
        public void Method_Condition_PinsCurrentResult()
        {
            var sut = new Calculator();

            var result = sut.Add(2, 3);

            Assert.AreEqual(5, result);
        }
        """,
        ["DataTestMethod"],
        "Assert.AreEqual({0}, {1});");

    private static readonly FrameworkInfo NUnit = new(
        TestFramework.NUnit, "Test", "TestFixture",
        ["NUnit.Framework"],
        ["Ignore", "Assert.Inconclusive", "Assert.Ignore"],
        ["Assert.Throws", "Assert.ThrowsAsync", "Assert.That"],
        """
        [Test]
        public void Method_Condition_PinsCurrentResult()
        {
            var sut = new Calculator();

            var result = sut.Add(2, 3);

            Assert.That(result, Is.EqualTo(5));
        }
        """,
        ["TestCase", "TestCaseSource"],
        "Assert.That({1}, Is.EqualTo({0}));");

    private static FrameworkInfo Xunit(TestFramework framework) => new(
        framework, "Fact", null,
        ["Xunit"],
        ["Skip ="],
        ["Assert.Throws", "Assert.ThrowsAsync", "Assert.ThrowsAny", "Assert.ThrowsAnyAsync"],
        """
        [Fact]
        public void Method_Condition_PinsCurrentResult()
        {
            var sut = new Calculator();

            var result = sut.Add(2, 3);

            Assert.Equal(5, result);
        }
        """,
        ["Theory"],
        "Assert.Equal({0}, {1});");

    public static FrameworkInfo Info(TestFramework f) => f switch
    {
        TestFramework.XunitV2 => XunitV2,
        TestFramework.XunitV3 => XunitV3,
        TestFramework.MsTest => MsTest,
        TestFramework.NUnit => NUnit,
        _ => throw new ArgumentOutOfRangeException(nameof(f)),
    };

    /// <summary>Reads SDK <c>PackageReference</c> items and a sibling <c>packages.config</c>.</summary>
    public static (TestFramework? Framework, string? Error) Detect(IFileSystem fs, string testCsproj)
    {
        var found = new SortedSet<TestFramework>();
        foreach (var id in PackageIds(fs, testCsproj))
        {
            if (Classify(id) is { } framework)
                found.Add(framework);
        }

        return found.Count switch
        {
            0 => (null, "테스트 프레임워크를 찾을 수 없습니다"),
            1 => (found.Min, null),
            _ => (null, $"테스트 프레임워크가 여럿입니다: {string.Join(", ", found)}"),
        };
    }

    private static TestFramework? Classify(string id)
    {
        if (id.StartsWith("xunit.v3", StringComparison.OrdinalIgnoreCase)) return TestFramework.XunitV3;
        if (id.Equals("xunit", StringComparison.OrdinalIgnoreCase)
            || id.Equals("xunit.core", StringComparison.OrdinalIgnoreCase)) return TestFramework.XunitV2;
        if (id.Equals("MSTest.TestFramework", StringComparison.OrdinalIgnoreCase)) return TestFramework.MsTest;
        if (id.Equals("NUnit", StringComparison.OrdinalIgnoreCase)) return TestFramework.NUnit;
        return null;
    }

    /// <summary>The mocking and assertion packages among the project's package ids (case-insensitive).</summary>
    public static TestLibraries Libraries(IFileSystem fs, string testCsproj)
    {
        var found = TestLibraries.None;
        foreach (var id in PackageIds(fs, testCsproj))
        {
            foreach (var library in Enum.GetValues<TestLibraries>())
                if (library != TestLibraries.None && id.Equals(library.ToString(), StringComparison.OrdinalIgnoreCase))
                    found |= library;
        }
        return found;
    }

    private static IEnumerable<string> PackageIds(IFileSystem fs, string testCsproj)
    {
        foreach (var id in Attributes(fs, testCsproj, "PackageReference", "Include"))
            yield return id;

        var dir = fs.Path.GetDirectoryName(testCsproj);
        if (dir is null) yield break;
        foreach (var id in Attributes(fs, fs.Path.Combine(dir, "packages.config"), "package", "id"))
            yield return id;
    }

    private static IEnumerable<string> Attributes(IFileSystem fs, string path, string element, string attribute)
    {
        if (!fs.File.Exists(path)) return [];
        try
        {
            var doc = XDocument.Parse(fs.File.ReadAllText(path));
            return doc.Descendants()
                .Where(e => e.Name.LocalName == element)
                .Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == attribute)?.Value)
                .OfType<string>()
                .ToList();
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }
}
