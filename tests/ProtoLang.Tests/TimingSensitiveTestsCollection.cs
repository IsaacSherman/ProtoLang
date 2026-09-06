using Xunit;

namespace ProtoLang.Tests;

// These tests exercise their own concurrency and hang guards. Unrelated compiler builds and
// mutation sweeps must not consume the scheduling budget needed to reach their assertions.
[CollectionDefinition("Timing-sensitive regressions", DisableParallelization = true)]
public sealed class TimingSensitiveTestsCollection;
