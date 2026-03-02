namespace DotNetGraphScanner.Graph;

public enum EdgeKind
{
    /// <summary>Parent→child containment (e.g. Project → Class, Class → Method).</summary>
    Contains,

    /// <summary>Method A invokes Method B.</summary>
    Calls,

    /// <summary>Class inherits from another class.</summary>
    Inherits,

    /// <summary>Class implements an interface.</summary>
    Implements,

    /// <summary>Project references another project.</summary>
    ProjectReference,

    /// <summary>Project depends on a NuGet package.</summary>
    PackageReference,

    /// <summary>Marks a node that is a known entry-point.</summary>
    EntryPoint,

    /// <summary>Method reads or writes a property.</summary>
    Accesses,

    /// <summary>Type or method is decorated with an attribute class.</summary>
    UsesAttribute,

    /// <summary>
    /// A method calls an endpoint on an external API.
    /// The edge's target is a virtual node representing the external API endpoint.
    /// </summary>
    ExternalApiCall
}
