using Xunit;

// A single shared browser + shared MySQL connection drives every test in this assembly
// sequentially (see ClaimAndMatchFixture); parallelizing test classes would race both.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
