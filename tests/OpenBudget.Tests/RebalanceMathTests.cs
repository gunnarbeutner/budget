using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class RebalanceMathTests
{
    private static RebalanceInput Item(string key, decimal current) =>
        new() { Key = key, Name = key, CurrentValue = current };

    [Fact]
    public void Ratio_splits_pool_in_proportion_to_weights()
    {
        var items = new[] { Item("A", 600m), Item("B", 400m) };
        var targets = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["A"] = 1m, ["B"] = 1m };

        var plan = RebalanceMath.ComputePlan(items, cash: 0m, RebalanceTargetMode.Ratio, targets);

        Assert.Equal(1000m, plan.Pool);
        Assert.Equal(500m, plan.Rows.Single(r => r.Key == "A").TargetValue);
        Assert.Equal(-100m, plan.Rows.Single(r => r.Key == "A").Delta);  // sell 100
        Assert.Equal(100m, plan.Rows.Single(r => r.Key == "B").Delta);   // buy 100
        Assert.Equal(100m, plan.TotalBuys);
        Assert.Equal(100m, plan.TotalSells);
        Assert.True(plan.Feasible);
    }

    [Fact]
    public void Ratio_with_cash_deploys_into_the_pool()
    {
        // Adding 1000 cash to a 900/100 book, targeting 50/50 → both land at 1000.
        var items = new[] { Item("A", 900m), Item("B", 100m) };
        var targets = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["A"] = 1m, ["B"] = 1m };

        var plan = RebalanceMath.ComputePlan(items, cash: 1000m, RebalanceTargetMode.Ratio, targets);

        Assert.Equal(2000m, plan.Pool);
        Assert.Equal(100m, plan.Rows.Single(r => r.Key == "A").Delta);   // buy 100
        Assert.Equal(900m, plan.Rows.Single(r => r.Key == "B").Delta);   // buy 900
        Assert.Equal(0m, plan.TotalSells);
    }

    [Fact]
    public void Amount_mode_reports_leftover_cash()
    {
        var items = new[] { Item("A", 600m), Item("B", 400m) };
        var targets = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["A"] = 700m, ["B"] = 400m };

        var plan = RebalanceMath.ComputePlan(items, cash: 200m, RebalanceTargetMode.Amount, targets);

        Assert.Equal(1200m, plan.Pool);
        Assert.Equal(100m, plan.Rows.Single(r => r.Key == "A").Delta);
        Assert.Equal(0m, plan.Rows.Single(r => r.Key == "B").Delta);
        Assert.Equal(100m, plan.Leftover);   // 1200 pool − 1100 targeted
        Assert.True(plan.Feasible);
    }

    [Fact]
    public void NoBuy_caps_a_securitys_target_at_its_current_value()
    {
        // Ratio 2:1 over a 1200 pool would push A to 800 (a 200 buy); marking A no-buy holds it at 600.
        var items = new[] { Item("A", 600m), Item("B", 400m) };
        var targets = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["A"] = 2m, ["B"] = 1m };
        var noBuy = new HashSet<string>(StringComparer.Ordinal) { "A" };

        var plan = RebalanceMath.ComputePlan(items, cash: 200m, RebalanceTargetMode.Ratio, targets, noBuy);

        Assert.Equal(0m, plan.Rows.Single(r => r.Key == "A").Delta);   // not bought past current
        Assert.Equal(600m, plan.Rows.Single(r => r.Key == "A").TargetValue);
    }

    [Fact]
    public void Whole_share_rounding_rounds_trades_to_units_and_holds_sub_share_moves()
    {
        // Price 100, current 1000 (10 shares). Target 1240 wants +2.4 shares → rounds to +2 (1200).
        Assert.Equal(1200m, RebalanceMath.QuantizeTarget(1000m, 1240m, price: 100m, wholeShares: true));
        // Target 1040 wants +0.4 shares → rounds to 0 → Hold at current.
        Assert.Equal(1000m, RebalanceMath.QuantizeTarget(1000m, 1040m, price: 100m, wholeShares: true));
        // Fractional assets (gold) keep the exact target.
        Assert.Equal(1040m, RebalanceMath.QuantizeTarget(1000m, 1040m, price: 100m, wholeShares: false));
    }

    [Fact]
    public void Sub_euro_deltas_are_suppressed_as_holds()
    {
        var items = new[] { Item("A", 1000m) };
        var targets = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["A"] = 1000.40m };

        var plan = RebalanceMath.ComputePlan(items, cash: 0m, RebalanceTargetMode.Amount, targets);

        Assert.Equal(0m, plan.Rows.Single().Delta); // 0.40 € is dust, not a trade
    }

    [Fact]
    public void Amount_mode_flags_over_allocation_as_infeasible()
    {
        var items = new[] { Item("A", 600m) };
        var targets = new Dictionary<string, decimal>(StringComparer.Ordinal) { ["A"] = 800m };

        var plan = RebalanceMath.ComputePlan(items, cash: 0m, RebalanceTargetMode.Amount, targets);

        Assert.False(plan.Feasible);   // wants 800 from a 600 pool
    }
}
