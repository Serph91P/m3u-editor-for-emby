using System;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class PluginSingletonCollectionTests
    {
        [Theory]
        [InlineData(typeof(ManagedPlaybackCacheEntryPointLifecycleTests))]
        [InlineData(typeof(ManagedPublishingTests))]
        [InlineData(typeof(ManagedOnlyContractTests))]
        [InlineData(typeof(PluginCompatibilityTests))]
        public void RuntimeConfigurationProducers_ShareThePluginSingletonCollection(Type fixture)
        {
            var collection = Assert.Single(fixture.CustomAttributes, attribute => attribute.AttributeType == typeof(CollectionAttribute));

            Assert.Equal(PluginSingletonCollection.Name, Assert.Single(collection.ConstructorArguments).Value);
        }
    }
}
