using System.Reflection;
using Xunit;

namespace QuickMatch.Tests;

/// <summary>
/// Every [HarmonyPatch(typeof(T), "Method")] in the built mod must name exactly one method declared
/// on T in the installed game. A wrong or ambiguous name only fails at game launch otherwise,
/// and game updates rename private methods. Uses MetadataLoadContext, so nothing is executed.
/// </summary>
public class PatchTargetTests
{
    private static string Metadata(string key) =>
        typeof(PatchTargetTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    [Fact]
    public void AllPatchTargetsResolveInInstalledGame()
    {
        string dataPath = Metadata("Sts2DataPath");
        string modDll = Metadata("QuickMatchDll");
        Assert.True(File.Exists(Path.Combine(dataPath, "sts2.dll")), $"game not found at {dataPath}");
        Assert.True(File.Exists(modDll), $"build the mod first: {modDll}");

        var paths = Directory.GetFiles(dataPath, "*.dll").Append(modDll);
        using var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");
        Assembly mod = context.LoadFromAssemblyPath(modDll);

        var problems = new List<string>();
        int checkedCount = 0;
        foreach (Type patchClass in mod.GetTypes())
        {
            foreach (CustomAttributeData attribute in patchClass.GetCustomAttributesData())
            {
                if (attribute.AttributeType.FullName != "HarmonyLib.HarmonyPatch" || attribute.ConstructorArguments.Count != 2)
                {
                    continue;
                }
                var target = (Type)attribute.ConstructorArguments[0].Value!;
                var methodName = (string)attribute.ConstructorArguments[1].Value!;
                MethodInfo[] matches = target
                    .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(m => m.Name == methodName)
                    .ToArray();
                checkedCount++;
                if (matches.Length != 1)
                {
                    problems.Add($"{patchClass.FullName}: {target.FullName}.{methodName} matched {matches.Length} methods");
                }
            }
        }

        Assert.True(checkedCount >= 24, $"expected at least 24 patches, found {checkedCount}");
        Assert.Empty(problems);
    }
}
