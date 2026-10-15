using NixManager.Domain;

namespace NixManager.Tests;

public sealed class PlannerTests
{
    [Test]
    public async Task Boot_defaults_to_the_system_profile_and_activates_on_next_boot()
    {
        var plan = Planner.Plan(TestConfig.Create(), Request.Create() with { Action = Actions.Boot, Scope = Scopes.System, Host = "test" });

        await Assert.That(plan.Steps.Select(step => step.Label)).IsEquivalentTo(["Build system configuration", "Activate system on next boot"]);
        await Assert.That(plan.Steps[1].Argv.TakeLast(3)).IsEquivalentTo(["activate-output", "boot", $"{TestConfig.Create().StateDir}/build/system"]);
    }
}
