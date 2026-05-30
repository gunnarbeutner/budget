namespace OpenBudget.Services;

/// <summary>
/// Best-effort solver for the "target a region (or any taxonomy) distribution" rebalance: given
/// each selected security's region-weight vector and a target distribution, find the fraction of
/// the pool to hold in each security so the combined look-through best matches the target.
///
/// It's a non-negative least squares with a sum-to-one constraint
/// (minimize ||A·f − target||² s.t. f ≥ 0, Σf = 1), solved by projected gradient descent with
/// Euclidean projection onto the probability simplex. Exact targets are often infeasible (e.g. no
/// selected security covers a wanted region), so the result is the closest achievable mix.
/// </summary>
public static class RebalanceSolver
{
    /// <summary>
    /// Finds the pool fraction per security whose combined region mix best matches the target.
    /// </summary>
    /// <param name="securityRegionWeights">
    /// One vector per security, each of length R (the region count), giving the security's fraction
    /// in each region. Each vector should sum to 1 (include an "unclassified" region for the
    /// remainder) so the achieved mix is a proper distribution.
    /// </param>
    /// <param name="target">Target fraction per region, length R, summing to 1.</param>
    /// <param name="upperBounds">
    /// Optional per-security cap on the pool fraction (length N). Use it to forbid *buying* a
    /// security: pass its current fraction so the solver can only hold or sell it. Null means no
    /// caps (each ≤ 1). When the caps sum to less than 1 the pool can't be fully invested, and the
    /// returned fractions sum to that lower total (the rest stays as cash).
    /// </param>
    /// <returns>Fraction of the pool per security: length N, each ≥ 0, ≤ its cap, summing to ≤ 1.</returns>
    public static double[] Solve(
        IReadOnlyList<double[]> securityRegionWeights,
        IReadOnlyList<double> target,
        IReadOnlyList<double>? upperBounds = null,
        int iterations = 4000)
    {
        int n = securityRegionWeights.Count;
        if (n == 0) return Array.Empty<double>();
        int r = target.Count;

        var caps = new double[n];
        double capSum = 0;
        for (int i = 0; i < n; i++) { caps[i] = upperBounds != null ? Math.Clamp(upperBounds[i], 0, 1) : 1.0; capSum += caps[i]; }
        // Invest the whole pool when the caps allow it; otherwise as much as they do (rest = cash).
        double budget = Math.Min(1.0, capSum);

        // Start at the capped budget spread within the caps.
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = capSum > 0 ? caps[i] / capSum * budget : 0.0;

        // Step size from a Lipschitz bound: L = 2·λmax(AᵀA) ≤ 2·‖A‖_F². A[k][i] = weights[i][k].
        double normF2 = 0;
        for (int i = 0; i < n; i++)
            for (int k = 0; k < r; k++)
                normF2 += securityRegionWeights[i][k] * securityRegionWeights[i][k];
        double eta = 1.0 / Math.Max(1e-9, 2.0 * normF2);

        var achieved = new double[r];
        var grad = new double[n];
        for (int it = 0; it < iterations; it++)
        {
            Array.Clear(achieved);
            for (int i = 0; i < n; i++)
            {
                var wi = securityRegionWeights[i];
                double fi = f[i];
                for (int k = 0; k < r; k++) achieved[k] += fi * wi[k];
            }
            for (int k = 0; k < r; k++) achieved[k] -= target[k]; // residual

            for (int i = 0; i < n; i++)
            {
                var wi = securityRegionWeights[i];
                double g = 0;
                for (int k = 0; k < r; k++) g += achieved[k] * wi[k];
                grad[i] = 2.0 * g;
            }

            for (int i = 0; i < n; i++) f[i] -= eta * grad[i];
            ProjectToCappedSimplex(f, caps, budget);
        }
        return f;
    }

    /// <summary>
    /// Euclidean projection onto { 0 ≤ xᵢ ≤ capᵢ, Σx = budget }. Finds the threshold τ with
    /// Σ clamp(vᵢ − τ, 0, capᵢ) = budget by bisection — the sum is monotonically decreasing in τ,
    /// so this converges robustly regardless of how many caps bind.
    /// </summary>
    private static void ProjectToCappedSimplex(double[] v, double[] caps, double budget)
    {
        int n = v.Length;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            lo = Math.Min(lo, v[i] - caps[i]); // τ at which xᵢ hits its cap
            hi = Math.Max(hi, v[i]);           // τ at which xᵢ hits 0
        }

        double a = lo - 1.0, b = hi + 1.0;
        for (int it = 0; it < 100; it++)
        {
            double tau = 0.5 * (a + b);
            double sum = 0;
            for (int i = 0; i < n; i++) sum += Math.Clamp(v[i] - tau, 0.0, caps[i]);
            if (sum > budget) a = tau; else b = tau; // sum decreases as τ grows
        }
        double t = 0.5 * (a + b);
        for (int i = 0; i < n; i++) v[i] = Math.Clamp(v[i] - t, 0.0, caps[i]);
    }
}
