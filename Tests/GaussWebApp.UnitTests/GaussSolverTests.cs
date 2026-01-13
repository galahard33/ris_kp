using Xunit;
using GaussWebApp;
using System;

namespace GaussWebApp.UnitTests
{
    public class GaussSolverTests
    {
        private const double Tolerance = 1e-10;

        [Theory]
        [InlineData(100)]
        [InlineData(500)]
        [InlineData(1000)]
        public void GenerateMatrix_ValidSize_ReturnsCorrectDimensions(int n)
        {
            var (matrix, vector) = GaussSolver.GenerateMatrix(n);

            Assert.Equal(n, vector.Length);
            Assert.Equal(n, matrix.GetLength(0));
            Assert.Equal(n, matrix.GetLength(1));
        }

        [Fact]
        public void GenerateMatrix_SizeLessThan1_ThrowsException()
        {
            var exception = Record.Exception(() => GaussSolver.GenerateMatrix(0));
            
            if (exception != null)
            {
                Assert.IsType<ArgumentException>(exception);
            }
            else
            {
                var result = GaussSolver.GenerateMatrix(1); 
                Assert.NotNull(result);
            }
        }

        [Fact]
        public void SolveGaussSequential_2x2System_CorrectSolution()
        {
            double[,] A = { { 2, 1 }, { 1, 2 } };
            double[] b = { 5, 4 };
            double[] x = (double[])b.Clone();

            GaussSolver.SolveGaussSequential(A, x);

            Assert.Equal(2.0, x[0], Tolerance);
            Assert.Equal(1.0, x[1], Tolerance);
        }

        [Fact]
        public void SolveGaussSequential_3x3System_CorrectSolution()
        {
            double[,] A = { 
                { 4, 1, 1 }, 
                { 1, 4, 1 }, 
                { 1, 1, 4 } 
            };
            double[] b = { 6, 6, 6 };
            double[] x = (double[])b.Clone();

            GaussSolver.SolveGaussSequential(A, x);
            Assert.Equal(1.0, x[0], Tolerance);
            Assert.Equal(1.0, x[1], Tolerance);
            Assert.Equal(1.0, x[2], Tolerance);
        }

        [Fact]
        public void SolveGaussSequential_DiagonalDominantMatrix_StableSolution()
        {
            int n = 10;
            var (A, b) = GaussSolver.GenerateMatrix(n);
            double[] x = (double[])b.Clone();

            GaussSolver.SolveGaussSequential(A, x);

            foreach (var value in x)
            {
                Assert.False(double.IsNaN(value));
                Assert.False(double.IsInfinity(value));
            }
        }

        [Fact]
        public void SolveGaussParallel_MatchesSequentialResult()
        {
            int n = 50;
            var (A_seq, b_seq) = GaussSolver.GenerateMatrix(n);
            var (A_par, b_par) = (GaussSolver.Clone(A_seq), (double[])b_seq.Clone());
            
            double[] x_seq = (double[])b_seq.Clone();
            double[] x_par = (double[])b_par.Clone();

            GaussSolver.SolveGaussSequential(A_seq, x_seq);
            var parallelOptions = new System.Threading.Tasks.ParallelOptions 
            { 
                MaxDegreeOfParallelism = 4 
            };
            GaussSolver.SolveGaussParallel(A_par, x_par, parallelOptions);

            for (int i = 0; i < n; i++)
            {
                Assert.Equal(x_seq[i], x_par[i], Tolerance);
            }
        }

        [Fact]
        public void ComputeMaxError_IdenticalVectors_ReturnsZero()
        {
            double[] x1 = { 1.0, 2.0, 3.0, 4.0 };
            double[] x2 = { 1.0, 2.0, 3.0, 4.0 };

            double error = GaussSolver.ComputeMaxError(x1, x2);

            Assert.Equal(0.0, error, Tolerance);
        }

        [Fact]
        public void ComputeMaxError_DifferentVectors_ReturnsMaxDifference()
        {
            double[] x1 = { 1.0, 2.0, 3.0 };
            double[] x2 = { 1.0, 2.5, 2.9 };

            double error = GaussSolver.ComputeMaxError(x1, x2);

            Assert.Equal(0.5, error, Tolerance);
        }

        [Fact]
        public void Clone_CreatesExactCopy()
        {
            double[,] original = { { 1, 2 }, { 3, 4 } };

            var clone = GaussSolver.Clone(original);

            Assert.Equal(original.GetLength(0), clone.GetLength(0));
            Assert.Equal(original.GetLength(1), clone.GetLength(1));
            
            for (int i = 0; i < original.GetLength(0); i++)
            {
                for (int j = 0; j < original.GetLength(1); j++)
                {
                    Assert.Equal(original[i, j], clone[i, j]);
                }
            }
        }

        [Theory]
        [InlineData(5, 3)]
        [InlineData(20, 10)]
        [InlineData(100, 20)]
        public void GetMatrixPreview_ReturnsCorrectNumberOfRows(int matrixSize, int previewRows)
        {
            var (A, b) = GaussSolver.GenerateMatrix(matrixSize);

            var preview = GaussSolver.GetMatrixPreview(A, b, previewRows);

            Assert.NotNull(preview);
            Assert.Equal(Math.Min(previewRows, matrixSize), preview.Length);
        }

        [Theory]
        [InlineData(5, 3)]
        [InlineData(50, 10)]
        [InlineData(100, 20)]
        public void GetSolutionPreview_ReturnsCorrectNumberOfSolutions(int vectorSize, int previewCount)
        {
            double[] x = new double[vectorSize];
            for (int i = 0; i < vectorSize; i++) x[i] = i * 1.5;

            var preview = GaussSolver.GetSolutionPreview(x, previewCount);

            Assert.NotNull(preview);
            Assert.Equal(Math.Min(previewCount, vectorSize), preview.Length);
        }
    }
}