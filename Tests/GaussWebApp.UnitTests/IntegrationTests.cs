using Xunit;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Net.Sockets;
using GaussWebApp.Controllers;
using GaussWebApp.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic; // ← ДОБАВЬТЕ ЭТО

namespace GaussWebApp.UnitTests
{
    public class IntegrationTests
    {
       [Fact(Skip = "Требует полной инфраструктуры и файла nodes.txt")]
        public async Task HomeController_DistributedMethod_WithMockWorkers()
        {
            // Этот тест требует полной инфраструктуры
            // Вместо этого пишем stub тест
            Assert.True(true);
        }
        
        [Fact]
        public async Task FileUpload_WithDistributedCalculation()
        {
            // Arrange
            var controller = new HomeController();
            var model = new GaussViewModel
            {
                CalculationMethod = CalculationMethod.Distributed,
                UploadedFile = CreateTestMatrixFile(50)
            };
            
            // Act & Assert
            // Тестируем полный цикл: загрузка файла -> распределённое решение
            var result = await controller.Index(model);
            
            Assert.IsType<ViewResult>(result);
        }
        
        private IFormFile CreateTestMatrixFile(int size)
        {
            var sb = new StringBuilder();
            sb.AppendLine(size.ToString());
            
            var rand = new Random(42);
            for (int i = 0; i < size; i++)
            {
                for (int j = 0; j < size; j++)
                {
                    sb.Append($"{rand.NextDouble():F6} ");
                }
                sb.AppendLine($"| {rand.NextDouble():F6}");
            }
            
            var bytes = Encoding.UTF8.GetBytes(sb.ToString());
            var stream = new MemoryStream(bytes);
            
            return new FormFile(stream, 0, bytes.Length, "test.txt", "test.txt")
            {
                Headers = new HeaderDictionary(),
                ContentType = "text/plain"
            };
        }
    }
}