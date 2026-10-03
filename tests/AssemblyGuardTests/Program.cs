using Mono.Cecil;
using Mono.Cecil.Cil;

// Guards which half of the Steamworks networking API AdaptiveNet calls. Steamworks.NET exposes
// the same surface twice — SteamNetworkingSockets/Utils guard on InteropHelp.TestIfAvailableClient,
// SteamGameServerNetworkingSockets/Utils on TestIfAvailableGameServer — and picking the wrong half
// does not fail to compile. It throws at runtime in a process that lacks that half's Steam context.
//
// There is no single right answer to hard-code, which is why this went in twice and was "fixed"
// twice, each pass repairing one process by breaking the other. Valheim ships two builds of
// assembly_valheim.dll and ZSteamSocket differs between them (Mono.Cecil, 2026-08-24):
//
//   valheim_Data\Managed        (client)    -> SteamNetworkingSockets / SteamNetworkingUtils
//   valheim_server_Data\Managed (dedicated) -> SteamGameServerNetworkingSockets / ...Utils
//
// A connection handle only exists inside the interface that created it, so the mod dispatches at
// runtime. The invariants below encode that design rather than either namespace:
//
//   1. Containment — every call into a split interface lives in SteamInterface, and nowhere else.
//      This is what stops a fourth flip: a direct call added anywhere else fails here.
//   2. Symmetry — SteamInterface calls BOTH halves of each operation it dispatches. A one-sided
//      dispatcher is the old bug wearing a new name.
//   3. Wiring — the three callers still reach the dispatcher, so 1 and 2 cannot pass vacuously
//      against an assembly where the sampling path was deleted or renamed.

string repoRoot = FindRepoRoot();
// Ignore switches so a stray "dotnet run --nologo" is not mistaken for a path override.
string? pathOverride = args.FirstOrDefault(arg => !arg.StartsWith('-'));
string assemblyPath = pathOverride != null
    ? Path.GetFullPath(pathOverride)
    : Path.Combine(repoRoot, "bin", "Release", "net48", "AdaptiveNet.dll");

if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine(
        $"Assembly guard tests need a Release build. Missing: {assemblyPath}\n" +
        "Run: dotnet build AdaptiveNet.csproj -c Release");
    return 1;
}

// This suite reads a build artifact, so it is only as truthful as the last build. MSBuild
// skips the recompile when a source file's timestamp is older than the output — restoring a
// file from a backup does exactly that — and the guard then happily validates code that is no
// longer in src\. Refuse to report on a stale assembly rather than passing against it.
DateTime assemblyWrittenAt = File.GetLastWriteTimeUtc(assemblyPath);
var newerSources = Directory
    .EnumerateFiles(Path.Combine(repoRoot, "src"), "*.cs", SearchOption.AllDirectories)
    .Where(source => File.GetLastWriteTimeUtc(source) > assemblyWrittenAt)
    .Select(Path.GetFileName)
    .ToList();
if (newerSources.Count > 0)
{
    Console.Error.WriteLine(
        $"{Path.GetFileName(assemblyPath)} is older than {newerSources.Count} source file(s): " +
        string.Join(", ", newerSources) + Environment.NewLine +
        "Rebuild before trusting this guard: dotnet build AdaptiveNet.csproj -c Release --no-incremental");
    return 1;
}

const string Dispatcher = "AdaptiveNet.SteamInterface";

// The four types whose choice is process-dependent. Deliberately narrower than "anything under
// Steamworks with Networking in the name": SteamNetworkingIdentity and friends are plain structs
// with no client/game-server split, so routing them through the dispatcher would be noise.
var splitInterfaces = new HashSet<string>(StringComparer.Ordinal)
{
    "Steamworks.SteamNetworkingSockets",
    "Steamworks.SteamGameServerNetworkingSockets",
    "Steamworks.SteamNetworkingUtils",
    "Steamworks.SteamGameServerNetworkingUtils",
};

// The game assemblies AdaptiveNet compiled against, written in by the csproj from ValheimInstall,
// so the literal-field guard (4.) can look at the fields the mod actually touches.
string valheimManaged = typeof(Test).Assembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
    .Cast<System.Reflection.AssemblyMetadataAttribute>()
    .Single(attribute => attribute.Key == "ValheimManaged").Value ?? string.Empty;
if (!File.Exists(Path.Combine(valheimManaged, "assembly_valheim.dll")))
{
    Console.Error.WriteLine(
        $"Assembly guard tests need the Valheim assemblies AdaptiveNet builds against. Missing: {valheimManaged}\n" +
        "Run with -p:ValheimInstall=\"path to Valheim\".");
    return 1;
}

var callSites = new List<CallSite>();
var literalFieldLoads = new List<CallSite>();
int resolvedGameStaticFields = 0;
using var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(valheimManaged);
using (AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
           assemblyPath, new ReaderParameters { AssemblyResolver = resolver }))
{
    foreach (TypeDefinition type in assembly.MainModule.GetTypes())
    {
        foreach (MethodDefinition method in type.Methods)
        {
            if (!method.HasBody) continue;
            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.Operand is FieldReference fieldReference &&
                    IsStaticFieldAccess(instruction.OpCode.Code) &&
                    TryResolve(fieldReference) is FieldDefinition field)
                {
                    if (field.Module.FileName.StartsWith(valheimManaged, StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedGameStaticFields++;
                    }
                    if (field.IsLiteral)
                    {
                        literalFieldLoads.Add(new CallSite(
                            type.FullName, method.Name, field.DeclaringType.FullName, field.Name));
                    }
                }

                if (instruction.Operand is not MemberReference member) continue;
                string declaring = member.DeclaringType?.FullName ?? string.Empty;
                if (!splitInterfaces.Contains(declaring) && declaring != Dispatcher) continue;
                callSites.Add(new CallSite(type.FullName, method.Name, declaring, member.Name));
            }
        }
    }
}

var splitSites = callSites.Where(site => splitInterfaces.Contains(site.DeclaringType)).ToList();

// A containment assertion passes vacuously against the wrong file, so prove the scan found the
// dispatcher's own calls before trusting its silence about everyone else's.
Test.True(splitSites.Count > 0, "the scan found Steamworks networking call sites at all");

// 1. Containment.
var strays = splitSites.Where(site => site.TypeName != Dispatcher).ToList();
Test.True(
    strays.Count == 0,
    "every split-interface call is confined to SteamInterface" + Describe(strays));

// 2. Symmetry: each dispatched operation calls both halves.
AssertSymmetry("GetConnectionRealTimeStatus",
    "Steamworks.SteamNetworkingSockets", "Steamworks.SteamGameServerNetworkingSockets");
AssertSymmetry("SetConfigValue",
    "Steamworks.SteamNetworkingUtils", "Steamworks.SteamGameServerNetworkingUtils");
AssertSymmetry("SendMessageToConnection",
    "Steamworks.SteamNetworkingSockets", "Steamworks.SteamGameServerNetworkingSockets");

// 3. Wiring: the callers still go through the dispatcher.
AssertDispatched("AdaptiveNet.SteamTransport", "TrySample", "GetConnectionRealTimeStatus",
    "the Steam sample reads real-time status through the dispatcher");
AssertDispatched("AdaptiveNet.SteamTransport", "SetInt", "SetConfigValue",
    "per-connection limits are set through the dispatcher");
AssertDispatched("AdaptiveNet.PinnedSteamSender", "TryHandle", "SendMessageToConnection",
    "the pinned sender sends through the dispatcher");

// 4. No static-field instruction names a const. A const is a `literal` field with no storage,
//    and Mono refuses to JIT any method that loads one with ldsfld ("Using static instructions
//    with literal field") — the whole method, not just that line, and only at runtime. Roslyn
//    normally folds the value in, yet it emitted ldsfld for ZRoutedRpc.Everybody in the vendored
//    ServerSync, which silently killed every config broadcast to connected clients.
Test.True(resolvedGameStaticFields > 0,
    "the scan resolved static fields of the game assemblies at all");
Test.True(
    literalFieldLoads.Count == 0,
    "no static-field instruction targets a const (literal) field" + Describe(literalFieldLoads));

Test.Summary();
return 0;

static bool IsStaticFieldAccess(Code code) =>
    code == Code.Ldsfld || code == Code.Ldsflda || code == Code.Stsfld;

static FieldDefinition? TryResolve(FieldReference reference)
{
    try
    {
        return reference.Resolve();
    }
    catch (AssemblyResolutionException)
    {
        // Framework assemblies are not on the search path; only the game's constants matter.
        return null;
    }
}

void AssertSymmetry(string memberName, string userType, string gameServerType)
{
    bool user = splitSites.Any(site =>
        site.TypeName == Dispatcher && site.MemberName == memberName && site.DeclaringType == userType);
    bool gameServer = splitSites.Any(site =>
        site.TypeName == Dispatcher && site.MemberName == memberName && site.DeclaringType == gameServerType);
    Test.True(
        user && gameServer,
        $"SteamInterface dispatches {memberName} to both halves (user={user}, gameServer={gameServer})");
}

void AssertDispatched(string typeName, string methodName, string memberName, string description)
{
    bool found = callSites.Any(site =>
        site.TypeName == typeName &&
        site.MethodName == methodName &&
        site.DeclaringType == Dispatcher &&
        site.MemberName == memberName);
    Test.True(found, description);
}

static string Describe(IReadOnlyCollection<CallSite> sites)
{
    if (sites.Count == 0) return string.Empty;
    return " — found: " + string.Join("; ", sites.Select(site =>
        $"{site.TypeName}.{site.MethodName} -> {site.DeclaringType}::{site.MemberName}"));
}

static string FindRepoRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AdaptiveNet.csproj")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName
        ?? throw new InvalidOperationException("Could not locate AdaptiveNet.csproj above the test binary.");
}

readonly record struct CallSite(string TypeName, string MethodName, string DeclaringType, string MemberName);

static class Test
{
    private static int _passed;

    public static void True(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{name}: condition was false");
        }

        _passed++;
    }

    public static void Summary() => Console.WriteLine($"Assembly guard tests passed: {_passed}");
}
