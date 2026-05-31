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
    public void A_pinned_security_is_forced_while_the_rest_fit_the_taxonomy()
    {
        // A is region 0, B and C are region 1. Pin A to 0.2 (lower = upper); the remaining 0.8 has
        // nowhere to go but region 1, so the mix lands at 0.2/0.8 — the closest to 50/50 with A fixed.
        var weights = new[]
        {
            new[] { 1.0, 0.0 },
            new[] { 0.0, 1.0 },
            new[] { 0.0, 1.0 }
        };
        var f = RebalanceSolver.Solve(weights, new[] { 0.5, 0.5 },
            upperBounds: new[] { 0.2, 1.0, 1.0 }, lowerBounds: new[] { 0.2, 0.0, 0.0 });

        Assert.Equal(0.2, f[0], precision: 3);          // forced to its pin
        Assert.Equal(0.8, f[1] + f[2], precision: 2);   // the free pool fills region 1
        Assert.Equal(1.0, f.Sum(), precision: 3);
    }

    [Fact]
    public void A_high_pin_forces_a_buy_above_the_taxonomy_target()
    {
        // Target wants only 50% region 0, but A (region 0) is pinned to 0.7 — it's forced there.
        var weights = new[]
        {
            new[] { 1.0, 0.0 },
            new[] { 0.0, 1.0 }
        };
        var f = RebalanceSolver.Solve(weights, new[] { 0.5, 0.5 },
            upperBounds: new[] { 0.7, 1.0 }, lowerBounds: new[] { 0.7, 0.0 });

        Assert.Equal(0.7, f[0], precision: 3);   // pinned above target
        Assert.Equal(0.3, f[1], precision: 3);
    }

    [Fact]
    public void An_exempt_security_is_ignored_by_the_fit_while_the_rest_hit_the_target()
    {
        // Exemption is modelled by zeroing the exempt security's objective vector and folding the
        // 50/50 target into the others (wᵢ − target) against a zero target. E is pinned to 0.5 and
        // ignored; A (region 0) and B (region 1) split the remaining 0.5 evenly to hit 50/50.
        var target = new[] { 0.5, 0.5 };
        var a = new[] { 1.0 - target[0], 0.0 - target[1] };
        var b = new[] { 0.0 - target[0], 1.0 - target[1] };
        var e = new[] { 0.0, 0.0 };   // exempt → zero objective vector

        var f = RebalanceSolver.Solve(
            new[] { a, b, e }, new[] { 0.0, 0.0 },
            upperBounds: new[] { 1.0, 1.0, 0.5 }, lowerBounds: new[] { 0.0, 0.0, 0.5 });

        Assert.Equal(0.5, f[2], precision: 3);   // pinned + ignored
        Assert.Equal(0.25, f[0], precision: 2);  // the rest balance to the target among themselves
        Assert.Equal(0.25, f[1], precision: 2);
    }

    [Fact]
    public void Turnover_penalty_avoids_churn_between_identical_securities()
    {
        // Two share classes of the same fund: identical region vectors, so the region objective is
        // indifferent to how they're split. Held 80/20 today, with the target already matching the
        // region mix — the min-turnover solution should leave them at 80/20, not reshuffle to 50/50.
        var weights = new[]
        {
            new[] { 1.0, 0.0 },
            new[] { 1.0, 0.0 }
        };
        var f = RebalanceSolver.Solve(
            weights, new[] { 1.0, 0.0 }, upperBounds: null, current: new[] { 0.8, 0.2 });

        Assert.Equal(0.8, f[0], precision: 2);
        Assert.Equal(0.2, f[1], precision: 2);
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
