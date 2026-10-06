// Fails unless the assembly reports the expected release version the way Agent.Host does (AgentSoftwareInfo.Detect):
// the informational version with build metadata after '+' dropped.
//   dotnet run check-assembly-version.cs -- <assembly.dll> <expected-version>
using System.Reflection;

if (args.Length != 2)
{
    Console.Error.WriteLine("usage: check-assembly-version.cs -- <assembly.dll> <expected-version>");
    return 2;
}

var informational = Assembly.LoadFrom(args[0]).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
var reported = informational?.Split('+', 2)[0];
Console.WriteLine($"{Path.GetFileName(args[0])}: informational version '{informational}', reported '{reported}', expected '{args[1]}'");
return reported == args[1] ? 0 : 1;
