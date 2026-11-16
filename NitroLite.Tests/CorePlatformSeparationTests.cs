using System.Reflection;
using System.Runtime.Versioning;
using NitroLite.Core;
using NitroLite.Core.Hardware;
using Xunit;

namespace NitroLite.Tests;

/// <summary>
/// Guards the split that makes a Linux front end possible: <c>NitroLite.Core</c> must stay loadable on a machine
/// with no WPF, no registry and no <c>System.Management</c>.
/// </summary>
/// <remarks>
/// The target framework already enforces most of this — a <c>net10.0</c> project cannot compile a call to
/// <c>System.Windows</c> — but two things it cannot catch are pinned here. The first is a reference that compiles
/// because the package happens to be multi-targeted (<c>System.Management</c> ships a netstandard build) and only
/// fails at run time on Linux. The second is the dependency arrow pointing the wrong way: the moment Core references
/// the WPF assembly, the "portable" half is no longer portable and nothing else would notice.
/// </remarks>
public class CorePlatformSeparationTests
{
    private static readonly Assembly Core = typeof(Loc).Assembly;

    /// <summary>
    /// Assemblies that exist only on Windows, or that only make sense there. A reference to any of them means the
    /// core assembly can no longer be loaded by a Linux front end.
    /// </summary>
    private static readonly string[] WindowsOnlyAssemblies =
    {
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Forms",
        "System.Windows.Extensions",
        "System.Management",
        "Microsoft.Win32.Registry",
        "Microsoft.Win32.SystemEvents",
        "Microsoft.Windows.SDK.NET",
        "System.Drawing.Common",
        "System.DirectoryServices",
        "System.ServiceProcess"
    };

    [Fact]
    public void Core_ReferencesNoWindowsOnlyAssembly()
    {
        var referenced = Core.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = WindowsOnlyAssemblies.Where(referenced.Contains).OrderBy(n => n, StringComparer.Ordinal).ToList();

        Assert.True(offenders.Count == 0,
            "NitroLite.Core references Windows-only assemblies, so it cannot load on Linux:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void Core_TargetsPlainNetAndNotTheWindowsFlavour()
    {
        var framework = Core.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        Assert.NotNull(framework);
        Assert.StartsWith(".NETCoreApp,Version=v", framework, StringComparison.Ordinal);
        Assert.DoesNotContain("windows", framework, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Core_DoesNotReferenceTheWpfAssembly()
    {
        // The arrow has to point one way: NitroLite -> NitroLite.Core. A reference back would make the core
        // assembly unloadable without WPF and would let a Windows type leak into the portable half.
        var referenced = Core.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("NitroLite", referenced);
    }

    [Fact]
    public void Core_CarriesTheModelTableAndEveryLanguageCatalogue()
    {
        // Both front ends read these from the core assembly, so a resource that stayed behind in the WPF project
        // would leave the Linux build with no model table and no translations.
        var resources = Core.GetManifestResourceNames();

        Assert.Contains(resources, n => n.EndsWith(".profiles.json", StringComparison.OrdinalIgnoreCase));
        foreach (var language in Loc.Languages.Where(l => l.Code != Loc.SourceLanguage))
        {
            Assert.Contains(resources, n => n.EndsWith($".Lang.{language.Code}.json", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Core_LoadsTheModelTableAndTheCataloguesFromItsOwnResources()
    {
        // The resource names above are only useful if the loaders actually find them. ProfileCatalog and Loc both
        // resolve through typeof(...).Assembly, so this is the end-to-end version of the check.
        var catalog = ProfileCatalog.Load();
        Assert.NotEmpty(catalog.Profiles);

        var english = Loc.LoadCatalog(Loc.DefaultLanguage);
        Assert.NotEmpty(english);
    }

    [Fact]
    public void Core_KeepsTheThermalAndPwmSafetyLimitsInCode()
    {
        // The floors are the one thing no profile file may loosen, so they are pinned here as well as in the
        // fan tests: a refactor that moved them into profiles.json would make this fail.
        Assert.Equal(new[] { (85, 60), (90, 80), (95, 100) }, FanCurve.SafetyFloor);
        Assert.Equal(30, FanCurve.MinPercent);
        Assert.Equal(100, FanCurve.MaxPercent);
    }
}
