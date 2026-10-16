using Xunit;

// One shared browser drives every test in this assembly sequentially (see AdminSpamReviewFixture),
// because the fixture's admin session and its seeded spam_records rows are both shared state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]