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
    /// <returns>Fraction of the pool per security: length N, each ≥ 0, summing to 1.</returns>
    public static double[] Solve(IReadOnlyList<double[]> securityRegionWeights, IReadOnlyList<double> target, int iterations = 4000)
    {
        int n = securityRegionWeights.Count;
        if (n == 0) return Array.Empty<double>();
        int r = target.Count;

        // Start uniform.
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = 1.0 / n;

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
            ProjectToSimplex(f);
        }
        return f;
    }

    /// <summary>Euclidean projection onto { x ≥ 0, Σx = 1 }. Standard sort-based algorithm.</summary>
    private static void ProjectToSimplex(double[] v)
    {
        int n = v.Length;
        var u = (double[])v.Clone();
        Array.Sort(u);
        Array.Reverse(u); // descending

        double cumulative = 0, theta = 0;
        int rho = -1;
        for (int j = 0; j < n; j++)
        {
            cumulative += u[j];
            double t = (cumulative - 1.0) / (j + 1);
            if (u[j] - t > 0) { rho = j; theta = t; }
        }
        if (rho < 0) theta = (cumulative - 1.0) / n;

        for (int i = 0; i < n; i++) v[i] = Math.Max(0.0, v[i] - theta);
    }
}
