// Fails unless the assembly reports the expected release version the way Agent.Host does (AgentSoftwareInfo.Detect):
// the informational version with build metadata after '+' dropped. Reads metadata only, so a cross-architecture assembly (linux-arm64) can be checked on an x64 runner.
//   dotnet run check-assembly-version.cs -- <assembly.dll> <expected-version>
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: check-assembly-version.cs -- <assembly.dll> <expected-version>");
    return 2;
}

using var stream = File.OpenRead(args[0]);
using var pe = new PEReader(stream);
var reader = pe.GetMetadataReader();
string? informational = null;
foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
{
    var attribute = reader.GetCustomAttribute(handle);
    if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
    var type = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
    if (type.Kind != HandleKind.TypeReference || reader.GetString(reader.GetTypeReference((TypeReferenceHandle)type).Name) != "AssemblyInformationalVersionAttribute") continue;
    var blob = reader.GetBlobReader(attribute.Value);
    blob.ReadUInt16(); // prolog
    informational = blob.ReadSerializedString();
}
var reported = informational?.Split('+', 2)[0];
Console.WriteLine($"{Path.GetFileName(args[0])}: informational version '{informational}', reported '{reported}', expected '{args[1]}'");
return reported == args[1] ? 0 : 1;
