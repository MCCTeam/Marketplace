using System.Threading.Tasks;
using DMCBK.PluginSdk;
public sealed class ExamplePlugin : IPlugin
{
    public void Configure(PluginDescriptor descriptor) => descriptor.Id = "example-plugin";
    public Task ActivateAsync(PluginContext context) => Task.CompletedTask;
}
