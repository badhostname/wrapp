namespace Wrapp.Models;

/// <summary>Lightweight SCCM app item for list display.</summary>
public class SCCMAppSummary
{
    public string CI_ID { get; init; } = "";
    public string Name { get; init; } = "";
    public string Manufacturer { get; init; } = "";
    public string SoftwareVersion { get; init; } = "";
    public string SiteCode { get; init; } = "";
    public int DeploymentCount { get; init; }
    public int DependencyCount { get; init; }
    public bool IsDeployed { get; init; }

    public string SearchText => $"{Name} {Manufacturer} {SoftwareVersion}".ToLowerInvariant();
}
