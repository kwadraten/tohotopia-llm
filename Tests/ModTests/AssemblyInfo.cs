using Xunit;

// Fixture statics (ModLoader overrides, game singletons) are shared across the
// assembly; every test belongs to the same collection and runs sequentially.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
