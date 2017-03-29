using Xunit;

// Some of our tests measure stress, GC pressure, etc.
// It messes with reliable test results when other threads are doing random stuff.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
