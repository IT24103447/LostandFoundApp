using Xunit;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ItemServiceIntegrationCollection : ICollectionFixture<ItemServiceApiFactory>
{
    public const string Name = "Item Service HTTP integration";
}
