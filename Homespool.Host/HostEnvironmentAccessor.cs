namespace Homespool.Host;

/// <summary>Supplies the content root without dragging <c>IWebHostEnvironment</c> into a test.</summary>
public sealed class HostEnvironmentAccessor : IHostEnvironmentAccessor
{
    public HostEnvironmentAccessor(string contentRootPath)
    {
        ContentRootPath = contentRootPath;
    }

    public string ContentRootPath { get; }
}
