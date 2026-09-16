using System.Xml.Linq;

namespace Helpaffe.UnitTests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Domain_has_no_package_or_project_references()
    {
        var repository = FindRepositoryRoot();
        var project = XDocument.Load(
            Path.Combine(repository, "src", "Helpaffe.Domain", "Helpaffe.Domain.csproj"));

        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("ProjectReference"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Helpaffe.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
