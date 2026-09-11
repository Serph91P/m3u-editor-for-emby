using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class PluginSingletonCollection
    {
        public const string Name = "Plugin singleton";
    }
}
