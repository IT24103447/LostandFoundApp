using Xunit;

// One shared browser drives every test in this assembly sequentially (see AdminAppealsFixture),
// because the fixture's admin session and its seeded appeal rows are both shared state - two
// classes filtering the same Pending tab at once would race each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]