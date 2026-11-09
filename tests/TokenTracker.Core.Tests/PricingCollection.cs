namespace TokenTracker.Core.Tests;

/// <summary>
/// ModelPricing overrides are process-wide static state, so the test classes that
/// set and reset them must never run at the same time: a reset in one test made an
/// assertion in the other read a zero cost (measured as a flaky failure on the Linux
/// runner, where class-level parallelism schedules them differently).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PricingCollection
{
    public const string Name = "pricing-overrides";
}
