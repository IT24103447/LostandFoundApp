using Xunit;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ItemServiceIntegrationCollection : ICollectionFixture<ItemServiceApiFactory>
{
    // xUnit collection marker: serializes MySQL-backed API tests that share the disposable database fixture.
    public const string Name = "Item Service HTTP integration";
}
