using Xunit;

// xUnit runs different test classes in parallel by default. Each class owns its own Chrome window,
// so that would open several browsers at once against one shared Vite dev server and one shared
// set of services, which makes everything slow and flaky. Run strictly one test at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
