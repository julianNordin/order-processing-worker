using System.Xml.Linq;

namespace OrderProcessing.UnitTests;

/// <summary>
/// The project boundaries are a design decision, not a convention, so they are asserted rather than
/// written down and hoped for. Both rules below are ones a reasonable person would break by accident:
/// adding a package to Contracts because it needed a JSON attribute, or referencing the API from the
/// worker because a DTO already existed there.
///
/// These read the project files rather than the compiled assemblies deliberately. A reference that
/// exists but is unused disappears from the compiled output, so an assembly-level check would pass
/// while the .csproj still declared the dependency - and it is the declaration that lets the next
/// person reach for it.
/// </summary>
public class ArchitectureTests
{
    [Fact]
    public void Contracts_declares_no_dependencies_of_any_kind()
    {
        var contracts = LoadProject("src/OrderProcessing.Contracts/OrderProcessing.Contracts.csproj");

        var projectReferences = ReferencedProjects(contracts);
        var packageReferences = contracts.Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? "")
            .ToArray();

        Assert.Empty(projectReferences);
        Assert.Empty(packageReferences);
    }

    [Theory]
    [InlineData("Api", "Worker")]
    [InlineData("Worker", "Api")]
    public void The_two_services_do_not_reference_each_other(string service, string other)
    {
        var project = LoadProject($"src/OrderProcessing.{service}/OrderProcessing.{service}.csproj");

        Assert.DoesNotContain($"OrderProcessing.{other}", ReferencedProjects(project));
    }

    [Fact]
    public void Every_project_reference_points_at_a_file_that_exists()
    {
        // Cheap, but it catches the rename that compiled locally because the old obj/ was still warm.
        var projects = Directory.EnumerateFiles(RepositoryRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        foreach (var projectPath in projects)
        {
            var directory = Path.GetDirectoryName(projectPath)!;
            var includes = XDocument.Load(projectPath).Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value ?? "");

            foreach (var include in includes)
            {
                var resolved = Path.GetFullPath(Path.Combine(directory, include.Replace('\\', Path.DirectorySeparatorChar)));
                Assert.True(File.Exists(resolved), $"{Path.GetFileName(projectPath)} references a missing project: {include}");
            }
        }
    }

    private static string[] ReferencedProjects(XDocument project) =>
        project.Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")?.Value ?? ""))
            .ToArray();

    private static XDocument LoadProject(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Walks up from the test binaries until it finds the solution file. Tests run from
    /// bin/Debug/net10.0, so nothing else in this class can use a path relative to the current
    /// directory and still work under both `dotnet test` and a run from an editor.
    /// </summary>
    private static string RepositoryRoot { get; } = FindRepositoryRoot();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !directory.EnumerateFiles("OrderProcessing.slnx").Any())
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate OrderProcessing.slnx above " + AppContext.BaseDirectory);
    }
}
