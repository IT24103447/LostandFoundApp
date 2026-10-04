namespace MatchingService.Tests.Support;

/// <summary>
/// Every test class spinning up a real Testcontainers MySQL/Kafka container goes in one of these 5
/// buckets, so xUnit runs each bucket's classes sequentially while the 5 buckets run in parallel with
/// each other - bounded parallelism instead of unbounded (crashes Docker) or fully serial (correct but
/// slow). A thread-count setting (xunit.runner.json) doesn't work for this at all: awaiting
/// InitializeAsync frees the thread, so container startups race regardless of any thread cap. The 4
/// classes that spin up both a real Kafka broker and real MySQL at once are spread one per bucket, so
/// no single bucket's peak container count is worse than any other's.
/// </summary>
[CollectionDefinition("Docker Integration Tests 1")]
public sealed class DockerIntegrationCollection1
{
}

[CollectionDefinition("Docker Integration Tests 2")]
public sealed class DockerIntegrationCollection2
{
}

[CollectionDefinition("Docker Integration Tests 3")]
public sealed class DockerIntegrationCollection3
{
}

[CollectionDefinition("Docker Integration Tests 4")]
public sealed class DockerIntegrationCollection4
{
}

[CollectionDefinition("Docker Integration Tests 5")]
public sealed class DockerIntegrationCollection5
{
}

[CollectionDefinition("Docker Integration Tests 6")]
public sealed class DockerIntegrationCollection6
{
}

[CollectionDefinition("Docker Integration Tests 7")]
public sealed class DockerIntegrationCollection7
{
}

[CollectionDefinition("Docker Integration Tests 8")]
public sealed class DockerIntegrationCollection8
{
}

[CollectionDefinition("Docker Integration Tests 9")]
public sealed class DockerIntegrationCollection9
{
}

[CollectionDefinition("Docker Integration Tests 10")]
public sealed class DockerIntegrationCollection10
{
}

[CollectionDefinition("Docker Integration Tests 11")]
public sealed class DockerIntegrationCollection11
{
}

[CollectionDefinition("Docker Integration Tests 12")]
public sealed class DockerIntegrationCollection12
{
}
