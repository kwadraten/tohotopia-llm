using Xunit;

// One headless UI thread, shared Files.Root and mutable test windows: run all
// tests sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
