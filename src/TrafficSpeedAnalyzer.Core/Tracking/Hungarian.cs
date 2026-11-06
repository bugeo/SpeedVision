namespace TrafficSpeedAnalyzer.Core.Tracking;

/// <summary>
/// Assegnazione a costo minimo (algoritmo ungherese, versione O(n³) con potenziali)
/// per matrici rettangolari.
/// </summary>
public static class Hungarian
{
    /// <summary>
    /// Restituisce per ogni riga l'indice di colonna assegnato, oppure -1.
    /// Le coppie con costo superiore a <paramref name="maxCost"/> vengono scartate dopo l'assegnazione.
    /// </summary>
    public static int[] Solve(double[,] cost, double maxCost = double.PositiveInfinity)
    {
        int rows = cost.GetLength(0);
        int cols = cost.GetLength(1);
        var result = new int[rows];
        Array.Fill(result, -1);
        if (rows == 0 || cols == 0)
        {
            return result;
        }

        bool transpose = rows > cols;
        int n = transpose ? cols : rows; // righe della matrice di lavoro (n <= m)
        int m = transpose ? rows : cols;
        double C(int i, int j) => transpose ? cost[j, i] : cost[i, j];

        // Implementazione classica con indici da 1.
        var u = new double[n + 1];
        var v = new double[m + 1];
        var p = new int[m + 1];
        var way = new int[m + 1];
        for (int i = 1; i <= n; i++)
        {
            p[0] = i;
            int j0 = 0;
            var minv = new double[m + 1];
            var used = new bool[m + 1];
            Array.Fill(minv, double.PositiveInfinity);
            do
            {
                used[j0] = true;
                int i0 = p[j0];
                double delta = double.PositiveInfinity;
                int j1 = 0;
                for (int j = 1; j <= m; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    double cur = C(i0 - 1, j - 1) - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                for (int j = 0; j <= m; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                int j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        for (int j = 1; j <= m; j++)
        {
            if (p[j] == 0)
            {
                continue;
            }

            int r = transpose ? j - 1 : p[j] - 1;
            int c = transpose ? p[j] - 1 : j - 1;
            if (cost[r, c] <= maxCost)
            {
                result[r] = c;
            }
        }

        return result;
    }
}
