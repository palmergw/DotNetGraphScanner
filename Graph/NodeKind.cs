namespace DotNetGraphScanner.Graph;

public enum NodeKind
{
    Solution,
    Project,
    Namespace,
    Class,
    Interface,
    Struct,
    Enum,
    Method,
    Property,
    NuGetPackage,
    ExternalType
}
