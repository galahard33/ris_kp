using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.IO;
using System.Text;

namespace GaussWebApp
{
    public static class MatrixIO
{
    public static MemoryStream SaveMatrixToStream(double[,] A, double[] b)
    {
        int n = b.Length;
        var sb = new StringBuilder();
        sb.AppendLine(n.ToString());

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                sb.Append($"{A[i, j]:R} ");
            }
            sb.AppendLine($"| {b[i]:R}");
        }

        byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
        return new MemoryStream(data);
    }

    public static (double[,], double[]) LoadMatrixFromStream(Stream stream)
    {
        using var reader = new StreamReader(stream);
        string firstLine = reader.ReadLine() ?? throw new InvalidDataException("Файл пуст");
        int n = int.Parse(firstLine);

        var A = new double[n, n];
        var b = new double[n];

        for (int i = 0; i < n; i++)
        {
            string line = reader.ReadLine() ?? throw new InvalidDataException($"Строка {i} отсутствует");
            string[] parts = line.Split('|');
            if (parts.Length != 2) throw new InvalidDataException($"Неверный формат строки {i}");

            string[] aValues = parts[0].Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string bValue = parts[1].Trim();

            if (aValues.Length != n) throw new InvalidDataException($"Неверное число столбцов в строке {i}");

            for (int j = 0; j < n; j++)
                A[i, j] = double.Parse(aValues[j]);

            b[i] = double.Parse(bValue);
        }

        return (A, b);
    }
}

     public static class GaussSolver
    {
        public static (double[,], double[]) GenerateMatrix(int n)
        {
            var rand = new Random(42);
            var A = new double[n, n];
            var b = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int j = 0; j < n; j++)
                {
                    if (i != j)
                    {
                        A[i, j] = rand.NextDouble() * 2 - 1;
                        sum += Math.Abs(A[i, j]);
                    }
                }
                A[i, i] = sum + 1 + rand.NextDouble();
                b[i] = rand.NextDouble() * 10;
            }
            return (A, b);
        }

        public static double[,] Clone(double[,] src)
        {
            int n = src.GetLength(0);
            var dst = new double[n, n];
            Array.Copy(src, dst, src.Length);
            return dst;
        }

        public static void SolveGaussSequential(double[,] A, double[] x)
        {
            int n = x.Length;
            for (int k = 0; k < n; k++)
            {
                int maxRow = k;
                for (int i = k + 1; i < n; i++)
                    if (Math.Abs(A[i, k]) > Math.Abs(A[maxRow, k])) maxRow = i;
                if (maxRow != k)
                {
                    for (int j = k; j < n; j++)
                        (A[k, j], A[maxRow, j]) = (A[maxRow, j], A[k, j]);
                    (x[k], x[maxRow]) = (x[maxRow], x[k]);
                }
                for (int i = k + 1; i < n; i++)
                {
                    double f = A[i, k] / A[k, k];
                    x[i] -= f * x[k];
                    for (int j = k; j < n; j++)
                        A[i, j] -= f * A[k, j];
                }
            }
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = i + 1; j < n; j++)
                    x[i] -= A[i, j] * x[j];
                x[i] /= A[i, i];
            }
        }

        public static void SolveGaussParallel(double[,] A, double[] x, ParallelOptions opts)
        {
            int n = x.Length;
            for (int k = 0; k < n; k++)
            {
                int maxRow = k;
                for (int i = k + 1; i < n; i++)
                    if (Math.Abs(A[i, k]) > Math.Abs(A[maxRow, k])) maxRow = i;
                if (maxRow != k)
                {
                    for (int j = k; j < n; j++)
                        (A[k, j], A[maxRow, j]) = (A[maxRow, j], A[k, j]);
                    (x[k], x[maxRow]) = (x[maxRow], x[k]);
                }
                Parallel.For(k + 1, n, opts, i =>
                {
                    double f = A[i, k] / A[k, k];
                    x[i] -= f * x[k];
                    for (int j = k; j < n; j++)
                        A[i, j] -= f * A[k, j];
                });
            }
            for (int i = n - 1; i >= 0; i--)
            {
                for (int j = i + 1; j < n; j++)
                    x[i] -= A[i, j] * x[j];
                x[i] /= A[i, i];
            }
        }

        public static double ComputeMaxError(double[] x1, double[] x2)
        {
            double err = 0;
            for (int i = 0; i < x1.Length; i++)
                err = Math.Max(err, Math.Abs(x1[i] - x2[i]));
            return err;
        }

        public static string[] GetMatrixPreview(double[,] A, double[] b, int rows)
        {
            int n = b.Length;
            int r = Math.Min(rows, n);
            int cols = Math.Min(6, n);
            var lines = new string[r];
            for (int i = 0; i < r; i++)
            {
                var row = "";
                for (int j = 0; j < cols; j++)
                    row += $"{A[i, j]:F2} ";
                if (cols < n) row += "... ";
                row += $"| {b[i]:F2}";
                lines[i] = row;
            }
            return lines;
        }

        public static string[] GetSolutionPreview(double[] x, int count)
        {
            int n = Math.Min(count, x.Length);
            var lines = new string[n];
            for (int i = 0; i < n; i++)
                lines[i] = $"x[{i}] = {x[i]:F6}";
            return lines;
        }
    }
}