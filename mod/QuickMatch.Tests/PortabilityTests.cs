using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace QuickMatch.Tests;

/// <summary>
/// QuickMatch runs where Steamworks is missing (the iPhone port, Android's launcher). There the game's
/// primary platform is None and its local player ID is 1, while quick-match runs record players by
/// Steam ID, so the mod must ask its lobby backend, or the Steam platform, instead.
/// </summary>
public class PortabilityTests
{
    [Fact]
    public void ModNeverReadsThePrimaryPlatform()
    {
        string modDll = typeof(PortabilityTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "QuickMatchDll").Value!;
        Assert.True(File.Exists(modDll), $"build the mod first: {modDll}");

        using var stream = File.OpenRead(modDll);
        using var pe = new PEReader(stream);
        MetadataReader reader = pe.GetMetadataReader();
        var uses = new List<string>();
        foreach (MemberReferenceHandle handle in reader.MemberReferences)
        {
            MemberReference member = reader.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }
            TypeReference type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            string typeName = $"{reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}";
            string name = reader.GetString(member.Name);
            if (typeName == "MegaCrit.Sts2.Core.Platform.PlatformUtil" && name == "get_PrimaryPlatform")
            {
                uses.Add($"{typeName}.{name}");
            }
        }
        Assert.Empty(uses);
    }
}
