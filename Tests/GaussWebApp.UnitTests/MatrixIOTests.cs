using Xunit;
using GaussWebApp;
using System.IO;
using System.Text;
using System;
using System.Globalization;

namespace GaussWebApp.UnitTests
{
    public class MatrixIOTests
    {
         private readonly CultureInfo _originalCulture;
    
    public MatrixIOTests()
    {
        _originalCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }
    
    public void Dispose()
    {
        CultureInfo.CurrentCulture = _originalCulture;
    }
        [Fact]
        public void SaveMatrixToStream_ValidData_WritesCorrectFormat()
        {
            double[,] A = { { 1.5, 2.25 }, { 3.75, 4.125 } };
            double[] b = { 5.5, 6.75 };

            using var stream = MatrixIO.SaveMatrixToStream(A, b);
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            string content = reader.ReadToEnd();

            var lines = content.Trim().Split('\n');
            Assert.Equal("2", lines[0].Trim()); 
            Assert.Contains("1.5 2.25 | 5.5", lines[1]);
            Assert.Contains("3.75 4.125 | 6.75", lines[2]);
        }

        [Fact]
        public void LoadMatrixFromStream_ValidFormat_LoadsCorrectly()
        {
            string matrixData = @"2
                                1.5 2.25 | 5.5
                                3.75 4.125 | 6.75";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(matrixData));

            var (A, b) = MatrixIO.LoadMatrixFromStream(stream);

            Assert.Equal(2, b.Length);
            Assert.Equal(2, A.GetLength(0));
            Assert.Equal(2, A.GetLength(1));
            
            Assert.Equal(1.5, A[0, 0]);
            Assert.Equal(2.25, A[0, 1]);
            Assert.Equal(3.75, A[1, 0]);
            Assert.Equal(4.125, A[1, 1]);
            
            Assert.Equal(5.5, b[0]);
            Assert.Equal(6.75, b[1]);
        }

        [Fact]
        public void SaveAndLoadMatrix_RoundTrip_PreservesData()
        {
            double[,] originalA = { { 1.1, 2.2 }, { 3.3, 4.4 } };
            double[] originalB = { 5.5, 6.6 };

            using var stream = MatrixIO.SaveMatrixToStream(originalA, originalB);
            stream.Position = 0;
            var (loadedA, loadedB) = MatrixIO.LoadMatrixFromStream(stream);

            Assert.Equal(originalA[0, 0], loadedA[0, 0], 6);
            Assert.Equal(originalA[0, 1], loadedA[0, 1], 6);
            Assert.Equal(originalA[1, 0], loadedA[1, 0], 6);
            Assert.Equal(originalA[1, 1], loadedA[1, 1], 6);
            Assert.Equal(originalB[0], loadedB[0], 6);
            Assert.Equal(originalB[1], loadedB[1], 6);
        }

        [Fact]
        public void LoadMatrixFromStream_EmptyStream_ThrowsException()
        {
            using var emptyStream = new MemoryStream();

            Assert.Throws<InvalidDataException>(() => 
                MatrixIO.LoadMatrixFromStream(emptyStream));
        }

        [Fact]
        public void LoadMatrixFromStream_InvalidFirstLine_ThrowsException()
        {
            string invalidData = @"not_a_number
1 2 | 3";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalidData));

            Assert.Throws<FormatException>(() => 
                MatrixIO.LoadMatrixFromStream(stream));
        }

        [Fact]
        public void LoadMatrixFromStream_MissingSeparator_ThrowsException()
        {
            string invalidData = @"2
                                1 2 3
                                4 5 6";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalidData));

            Assert.Throws<InvalidDataException>(() => 
                MatrixIO.LoadMatrixFromStream(stream));
        }

        [Fact]
        public void LoadMatrixFromStream_WrongColumnCount_ThrowsException()
        {
            string invalidData = @"2
                                1 2 3 | 4
                                5 6 | 7";
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalidData));

            Assert.Throws<InvalidDataException>(() => 
                MatrixIO.LoadMatrixFromStream(stream));
        }

        [Fact]
        public void LoadMatrixFromStream_MissingRows_ThrowsException()
        {

            string invalidData = @"3
                                1 2 | 3
                                4 5 | 6";

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalidData));


            Assert.Throws<InvalidDataException>(() => 
                MatrixIO.LoadMatrixFromStream(stream));
        }
    }
}