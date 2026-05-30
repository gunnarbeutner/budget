using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class RebalanceSolverTests
{
    [Fact]
    public void Single_region_securities_hit_an_exact_target()
    {
        // A is purely region 0, B purely region 1. Target 70/30 → hold 70% A, 30% B.
        var weights = new[]
        {
            new[] { 1.0, 0.0 },
            new[] { 0.0, 1.0 }
        };
        var f = RebalanceSolver.Solve(weights, new[] { 0.7, 0.3 });

        Assert.Equal(0.7, f[0], precision: 2);
        Assert.Equal(0.3, f[1], precision: 2);
    }

    [Fact]
    public void Two_region_etf_and_pure_etfs_achieve_the_target_mix()
    {
        // A = 60/40 across regions; B pure region 0; C pure region 1. Target 60/40 is reachable
        // (by A alone, or a B/C blend) — assert the achieved mix, not a specific holding split.
        var weights = new[]
        {
            new[] { 0.6, 0.4 },
            new[] { 1.0, 0.0 },
            new[] { 0.0, 1.0 }
        };
        var f = RebalanceSolver.Solve(weights, new[] { 0.6, 0.4 });

        double r0 = f[0] * 0.6 + f[1] * 1.0 + f[2] * 0.0;
        double r1 = f[0] * 0.4 + f[1] * 0.0 + f[2] * 1.0;
        Assert.Equal(0.6, r0, precision: 2);
        Assert.Equal(0.4, r1, precision: 2);
    }

    [Fact]
    public void Infeasible_target_returns_closest_feasible_mix()
    {
        // Only region-0 exposure available, but the target wants region 1 too. Best effort: all
        // weight on the single security; result stays a valid distribution.
        var weights = new[] { new[] { 1.0, 0.0 } };
        var f = RebalanceSolver.Solve(weights, new[] { 0.5, 0.5 });

        Assert.Single(f);
        Assert.Equal(1.0, f[0], precision: 4);
    }

    [Fact]
    public void Upper_bound_caps_a_no_buy_security_and_routes_the_rest_elsewhere()
    {
        // A is region 0, B region 1; target wants 50/50, but A is capped at 0.2 (no buying it).
        var weights = new[]
        {
            new[] { 1.0, 0.0 },
            new[] { 0.0, 1.0 }
        };
        var f = RebalanceSolver.Solve(weights, new[] { 0.5, 0.5 }, upperBounds: new[] { 0.2, 1.0 });

        Assert.True(f[0] <= 0.2 + 1e-3);   // respects the cap
        Assert.Equal(0.8, f[1], precision: 2); // the rest goes to B
    }

    [Fact]
    public void When_every_security_is_capped_below_full_the_pool_is_under_invested()
    {
        var weights = new[]
        {
            new[] { 1.0, 0.0 },
            new[] { 0.0, 1.0 }
        };
        var f = RebalanceSolver.Solve(weights, new[] { 0.5, 0.5 }, upperBounds: new[] { 0.3, 0.3 });

        Assert.True(f[0] <= 0.3 + 1e-3);
        Assert.True(f[1] <= 0.3 + 1e-3);
        Assert.Equal(0.6, f.Sum(), precision: 2); // only 60% can be invested; rest stays cash
    }

    [Fact]
    public void Result_is_a_distribution_nonnegative_and_sums_to_one()
    {
        var weights = new[]
        {
            new[] { 0.5, 0.3, 0.2 },
            new[] { 0.1, 0.8, 0.1 },
            new[] { 0.0, 0.0, 1.0 }
        };
        var f = RebalanceSolver.Solve(weights, new[] { 0.4, 0.4, 0.2 });

        Assert.All(f, x => Assert.True(x >= -1e-9));
        Assert.Equal(1.0, f.Sum(), precision: 4);
    }
}
