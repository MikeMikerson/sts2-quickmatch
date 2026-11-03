using System.Reflection;
using Xunit;

namespace QuickMatch.Android.Tests;

/// <summary>
/// Every [HarmonyPatch(typeof(T), "Method")] in the built mod must name exactly one method declared on
/// T in the installed game (the Android launcher runs the same build, v0.111.0). A wrong or ambiguous
/// name only fails on the phone otherwise. Uses MetadataLoadContext, so nothing is executed.
/// </summary>
public class PatchTargetTests
{
    private static string Metadata(string key) =>
        typeof(PatchTargetTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    [Fact]
    public void AllPatchTargetsResolveInInstalledGame()
    {
        string dataPath = Metadata("Sts2DataPath");
        string modDll = Metadata("QuickMatchAndroidDll");
        Assert.True(File.Exists(Path.Combine(dataPath, "sts2.dll")), $"game not found at {dataPath}");
        Assert.True(File.Exists(modDll), $"build the mod first: {modDll}");

        // The mod's other references (QuickMatch, SteamKit2) are not needed to read its attributes.
        var paths = Directory.GetFiles(dataPath, "*.dll").Append(modDll);
        using var context = new MetadataLoadContext(new PathAssemblyResolver(paths), "System.Private.CoreLib");
        Assembly mod = context.LoadFromAssemblyPath(modDll);

        var found = new List<string>();
        var problems = new List<string>();
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
                int matches = target
                    .GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Count(m => m.Name == methodName);
                found.Add($"{target.Name}.{methodName}");
                if (matches != 1)
                {
                    problems.Add($"{patchClass.FullName}: {target.FullName}.{methodName} matched {matches} methods");
                }
            }
        }

        Assert.Equal(
            new[] { "RunManager.CanonicalizeSave", "SteamPlatformUtilStrategy.GetLocalPlayerId", "SteamPlatformUtilStrategy.GetPlayerName" },
            found.Order());
        Assert.Empty(problems);
    }
}
