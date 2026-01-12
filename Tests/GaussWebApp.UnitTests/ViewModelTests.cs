using Xunit;
using GaussWebApp.Models;
using Microsoft.AspNetCore.Http;
using System.IO;
using System.Text;

namespace GaussWebApp.UnitTests
{
    public class GaussViewModelTests
    {
        [Fact]
        public void GaussViewModel_DefaultValues_AreCorrect()
        {
            // Arrange & Act
            var model = new GaussViewModel();

            // Assert
            Assert.Equal(1000, model.MatrixSize);
            Assert.Equal(CalculationMethod.Parallel, model.CalculationMethod);
            Assert.Equal(4, model.ThreadCount);
            Assert.Equal(string.Empty, model.ErrorMessage);
            Assert.False(model.HasResult);
            Assert.False(model.RequestDownload);
        }

        [Theory]
        [InlineData(100)]
        [InlineData(1000)]
        [InlineData(5000)]
        public void GaussViewModel_MatrixSize_ValidRange(int size)
        {
            // Arrange
            var model = new GaussViewModel { MatrixSize = size };

            // Act & Assert
            Assert.Equal(size, model.MatrixSize);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        [InlineData(8)]
        [InlineData(16)]
        public void GaussViewModel_ThreadCount_ValidValues(int threadCount)
        {
            // Arrange
            var model = new GaussViewModel 
            { 
                CalculationMethod = CalculationMethod.Parallel,
                ThreadCount = threadCount 
            };

            // Act & Assert
            Assert.Equal(threadCount, model.ThreadCount);
        }

        [Fact]
        public void GaussViewModel_FileUpload_CanBeSet()
        {
            // Arrange
            var model = new GaussViewModel();
            var file = new FormFile(
                new MemoryStream(Encoding.UTF8.GetBytes("test")),
                0, 4, "test.txt", "test.txt");

            // Act
            model.UploadedFile = file;

            // Assert
            Assert.NotNull(model.UploadedFile);
            Assert.Equal("test.txt", model.UploadedFile.FileName);
        }
    }

    public class CalculationMethodTests
    {
        [Fact]
        public void CalculationMethod_HasCorrectValues()
        {
            // Arrange & Act
            var parallel = CalculationMethod.Parallel;
            var distributed = CalculationMethod.Distributed;

            // Assert
            Assert.Equal(0, (int)parallel);
            Assert.Equal(1, (int)distributed);
        }
    }
}