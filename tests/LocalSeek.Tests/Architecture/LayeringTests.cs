using System.Reflection;

namespace LocalSeek.Tests.Architecture;

public class LayeringTests
{
    private static readonly string[] ForbiddenPrefixes =
    [
        "Uno",
        "Microsoft.UI",
        "Microsoft.WinUI",
        "Microsoft.Windows",
        "Windows",
        "PresentationFramework",
        "System.Windows.Forms",
        "WinRT",
    ];

    public static TheoryData<string> PureAssemblies => new()
    {
        "LocalSeek.Core",
        "LocalSeek.Indexing",
        "LocalSeek.App.ViewModels",
    };

    [Theory]
    [MemberData(nameof(PureAssemblies))]
    public void Pure_assemblies_do_not_reference_ui_or_windows_specific_assemblies(string assemblyName)
    {
        var references = Assembly.Load(assemblyName).GetReferencedAssemblies();

        var offending = references
            .Select(r => r.Name!)
            .Where(n => ForbiddenPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void Core_does_not_reference_other_localseek_assemblies()
    {
        var references = Assembly.Load("LocalSeek.Core").GetReferencedAssemblies();

        Assert.DoesNotContain(references, r => r.Name!.StartsWith("LocalSeek.", StringComparison.Ordinal));
    }

    [Fact]
    public void Indexing_does_not_reference_view_models()
    {
        var references = Assembly.Load("LocalSeek.Indexing").GetReferencedAssemblies();

        Assert.DoesNotContain(references, r => r.Name == "LocalSeek.App.ViewModels");
    }
}
