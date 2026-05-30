using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Pure per-security rebalance math. The pool is the selected securities' current value plus any
/// injected cash; targets are either absolute amounts or relative weights, and each security's
/// trade (buy/sell) is the gap between its target value and its current value.
/// </summary>
public static class RebalanceMath
{
    public static RebalancePlan ComputePlan(
        IReadOnlyList<RebalanceInput> items,
        decimal cash,
        RebalanceTargetMode mode,
        IReadOnlyDictionary<string, decimal> targets)
    {
        decimal selectedCurrent = items.Sum(i => i.CurrentValue);
        decimal pool = selectedCurrent + cash;

        // Ratio mode splits the pool in proportion to the (non-negative) weights.
        decimal sumWeights = mode == RebalanceTargetMode.Ratio
            ? items.Sum(i => Math.Max(0m, Target(targets, i.Key)))
            : 0m;

        var rows = new List<RebalanceRow>(items.Count);
        foreach (var i in items)
        {
            decimal t = Math.Max(0m, Target(targets, i.Key));
            decimal targetValue = mode switch
            {
                RebalanceTargetMode.Amount => t,
                RebalanceTargetMode.Ratio => sumWeights > 0m ? t / sumWeights * pool : 0m,
                _ => 0m
            };
            rows.Add(new RebalanceRow
            {
                Key = i.Key,
                Name = i.Name,
                CurrentValue = i.CurrentValue,
                CurrentFraction = pool > 0m ? i.CurrentValue / pool : 0m,
                TargetValue = targetValue,
                TargetFraction = pool > 0m ? targetValue / pool : 0m,
                Delta = targetValue - i.CurrentValue
            });
        }

        decimal totalTarget = rows.Sum(r => r.TargetValue);
        decimal totalBuys = rows.Where(r => r.Delta > 0m).Sum(r => r.Delta);
        decimal totalSells = rows.Where(r => r.Delta < 0m).Sum(r => -r.Delta);
        decimal leftover = pool - totalTarget;

        return new RebalancePlan
        {
            Pool = pool,
            Cash = cash,
            TotalBuys = totalBuys,
            TotalSells = totalSells,
            Leftover = Math.Max(0m, leftover),
            // A tiny rounding slack so a target set that sums to exactly the pool isn't flagged.
            Feasible = leftover >= -0.005m,
            Rows = rows
        };
    }

    private static decimal Target(IReadOnlyDictionary<string, decimal> targets, string key) =>
        targets.TryGetValue(key, out var v) ? v : 0m;
}
