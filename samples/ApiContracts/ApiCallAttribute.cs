namespace ApiContracts;

/// <summary>
/// Marks a method as an outbound HTTP call to another API.
/// Apply this attribute to typed-client interface methods or their implementations
/// so that the dotnet-graph-scanner can trace cross-API dependencies.
/// </summary>
/// <example>
/// <code>
/// [ApiCall("BarApi", "GET /inventory")]
/// Task&lt;InventoryResponse&gt; GetInventoryAsync();
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ApiCallAttribute : Attribute
{
    /// <summary>The logical name of the target API (must match the project/solution name).</summary>
    public string Api { get; }

    /// <summary>HTTP method and route path of the target endpoint, e.g. "GET /inventory".</summary>
    public string Route { get; }

    public ApiCallAttribute(string api, string route)
    {
        Api   = api;
        Route = route;
    }
}
