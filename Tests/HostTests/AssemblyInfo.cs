using Xunit;

// Files.Root and credential storage are mutable global state; keep tests
// sequential so temp roots and saved credentials do not race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
