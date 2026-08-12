using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Stdio is the transport, so stdout belongs to the protocol — nothing else may write to it.
// The empty builder is deliberate: it adds no configuration file watching, which hangs host
// construction forever when the exe runs from the WSL UNC share.
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
{
    ApplicationName = "DropZone.Mcp",
    ContentRootPath = Path.GetTempPath()
});

builder.Services.AddLogging();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
