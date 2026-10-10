namespace AdminVerifyService.Tests.Support;

/// <summary>
/// Every class that spins up a real Testcontainers container (MySQL and/or Kafka) joins this single
/// collection so xUnit executes those classes sequentially. Serial is a deliberate choice over the
/// bucketed-parallel approach in MatchingService.Tests: this dev/QA machine starts one container
/// stack at a time, which keeps every class deterministic on Windows and on the Linux CI runner
/// (the LF-338 platform-consistency lesson). If the suite grows and runtime becomes a concern,
/// split this into per-feature buckets like MatchingService.Tests Support/DockerIntegrationCollection.
/// </summary>
[CollectionDefinition("AdminVerify Service Docker Integration")]
public sealed class AdminVerifyDockerCollection
{
}