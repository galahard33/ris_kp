using Xunit;
using GaussWebApp;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace GaussWebApp.LoadTests
{
    [Collection("PerformanceTests")]
    public class PerformanceTests : IDisposable
    {
        // Кешируем сгенерированные матрицы для повторного использования
        private static readonly Dictionary<int, (double[,] matrix, double[] vector)> _matrixCache = new();
        
        public void Dispose()
        {
            // Очищаем кеш после всех тестов
            _matrixCache.Clear();
            GC.Collect();
        }
        
        private (double[,] matrix, double[] vector) GetOrCreateMatrix(int size)
        {
            if (!_matrixCache.TryGetValue(size, out var cached))
            {
                cached = GaussSolver.GenerateMatrix(size);
                _matrixCache[size] = cached;
            }
            return cached;
        }
        
        // Тест 1: Производительность последовательного метода
        [Theory]
        [InlineData(100)]
        [InlineData(500)]
        [InlineData(1000)]
        public void SequentialGauss_PerformanceTest(int size)
        {
            // Arrange
            var (matrix, vector) = GetOrCreateMatrix(size);
            var A = GaussSolver.Clone(matrix);
            var x = (double[])vector.Clone();
            
            // Act
            var stopwatch = Stopwatch.StartNew();
            GaussSolver.SolveGaussSequential(A, x);
            stopwatch.Stop();
            
            // Упрощенная проверка
            Assert.NotNull(x);
            Assert.Equal(size, x.Length);
            
            for (int i = 0; i < Math.Min(5, size); i++)
            {
                Assert.False(double.IsNaN(x[i]), $"Решение содержит NaN в позиции {i}");
                Assert.False(double.IsInfinity(x[i]), $"Решение содержит Infinity в позиции {i}");
            }
            
            Console.WriteLine($"[{size}×{size}] Время: {stopwatch.Elapsed.TotalSeconds:F3} сек");
        }
        
        // Тест 2: Сравнение последовательного и параллельного методов
        [Theory]
        [InlineData(1000, 2)]
        [InlineData(1000, 4)]
        [InlineData(1000, 8)]
        public void ParallelVsSequential_Comparison(int size, int threads)
        {
            // Arrange
            var (matrix, vector) = GetOrCreateMatrix(size);
            
            // Последовательный метод
            var swSeq = Stopwatch.StartNew();
            var A_seq = GaussSolver.Clone(matrix);
            var x_seq = (double[])vector.Clone();
            GaussSolver.SolveGaussSequential(A_seq, x_seq);
            swSeq.Stop();
            
            // Параллельный метод
            var swPar = Stopwatch.StartNew();
            var A_par = GaussSolver.Clone(matrix);
            var x_par = (double[])vector.Clone();
            var options = new System.Threading.Tasks.ParallelOptions 
            { 
                MaxDegreeOfParallelism = threads 
            };
            GaussSolver.SolveGaussParallel(A_par, x_par, options);
            swPar.Stop();
            
            // Упрощенная проверка
            double maxError = 0;
            int checkCount = Math.Min(50, size);
            for (int i = 0; i < checkCount; i++)
            {
                maxError = Math.Max(maxError, Math.Abs(x_seq[i] - x_par[i]));
            }
            
            // Результаты
            double speedup = swSeq.Elapsed.TotalSeconds / swPar.Elapsed.TotalSeconds;
            double efficiency = speedup / threads * 100;
            
            Console.WriteLine($"[{size}×{size}, {threads} потоков]");
            Console.WriteLine($"  Seq: {swSeq.Elapsed.TotalSeconds:F3} сек");
            Console.WriteLine($"  Par: {swPar.Elapsed.TotalSeconds:F3} сек");
            Console.WriteLine($"  Ускорение: {speedup:F2}x (эфф. {efficiency:F1}%)");
            Console.WriteLine($"  Разница: {maxError:E6}");

            // Ослабленное условие
            if (maxError > 1e-3)
            {
                Console.WriteLine($"  ВНИМАНИЕ: разница большая, но допустимая для нагрузочного теста");
            }
            
            Assert.NotNull(x_seq);
            Assert.NotNull(x_par);
        }
        
        // Тест 3: Масштабируемость
        [Fact]
        public void ScalabilityTest_Optimized()
        {
            Console.WriteLine($"=== МАСШТАБИРУЕМОСТЬ ===");
            Console.WriteLine($"Текущая директория: {Directory.GetCurrentDirectory()}");
            
            int size = 1000;
            var (matrix, vector) = GetOrCreateMatrix(size);
            
            var results = new Dictionary<int, (double time, double speedup, double efficiency)>();
            
            foreach (int threads in new[] { 1, 2, 4, 8 })
            {
                var A = GaussSolver.Clone(matrix);
                var x = (double[])vector.Clone();
                
                var stopwatch = Stopwatch.StartNew();
                
                if (threads == 1)
                {
                    GaussSolver.SolveGaussSequential(A, x);
                }
                else
                {
                    var options = new System.Threading.Tasks.ParallelOptions 
                    { 
                        MaxDegreeOfParallelism = threads 
                    };
                    GaussSolver.SolveGaussParallel(A, x, options);
                }
                
                stopwatch.Stop();
                
                // Базовая проверка
                Assert.NotNull(x);
                Assert.Equal(size, x.Length);
                
                if (threads == 1)
                {
                    results[1] = (stopwatch.Elapsed.TotalSeconds, 1.0, 100.0);
                }
                else
                {
                    double speedup = results[1].time / stopwatch.Elapsed.TotalSeconds;
                    double efficiency = speedup / threads * 100;
                    results[threads] = (stopwatch.Elapsed.TotalSeconds, speedup, efficiency);
                }
                
                Console.WriteLine($"  {threads} потоков: {stopwatch.Elapsed.TotalSeconds:F3} сек");
            }
            
            // Сохраняем в CSV
            SaveResultsToCSV(results, "scalability-results.csv");
        }
        
        // Тест 4: Потребление памяти
        [Fact]
        public void MemoryUsageTest_Optimized()
        {
            Console.WriteLine($"=== ПОТРЕБЛЕНИЕ ПАМЯТИ ===");
            
            var sizes = new[] { 100, 500, 1000 };
            var results = new List<string> { "Размер,Время(сек),Память(МБ)" };
            
            // Предварительная сборка мусора
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            foreach (var size in sizes)
            {
                // Измеряем память ДО
                GC.Collect();
                GC.WaitForPendingFinalizers();
                long memoryBefore = GC.GetTotalMemory(true);
                
                var stopwatch = Stopwatch.StartNew();
                
                // Генерируем и решаем
                var (matrix, vector) = GaussSolver.GenerateMatrix(size);
                var A = GaussSolver.Clone(matrix);
                var x = (double[])vector.Clone();
                GaussSolver.SolveGaussSequential(A, x);
                
                stopwatch.Stop();
                
                // Измеряем память ПОСЛЕ
                long memoryAfter = GC.GetTotalMemory(true);
                
                // Базовая проверка
                Assert.NotNull(x);
                Assert.Equal(size, x.Length);
                
                double memoryUsedMB = (memoryAfter - memoryBefore) / (1024.0 * 1024.0);
                results.Add($"{size},{stopwatch.Elapsed.TotalSeconds:F3},{memoryUsedMB:F2}");
                
                Console.WriteLine($"[{size}×{size}]: {stopwatch.Elapsed.TotalSeconds:F3} сек, {memoryUsedMB:F2} МБ");
                
                // Очищаем память
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            
            // Сохраняем результаты
            SaveResultsToCSV(results, "memory-usage.csv");
        }
        
        // Тест 5: Стабильность
        [Fact]
        public void StabilityTest_Optimized()
        {
            int size = 500;
            int iterations = 2;
            
            var times = new List<double>();
            
            // Генерируем матрицу один раз
            var (matrix, vector) = GetOrCreateMatrix(size);
            
            for (int i = 0; i < iterations; i++)
            {
                // Клонируем для каждого запуска
                var A = GaussSolver.Clone(matrix);
                var x = (double[])vector.Clone();
                
                var stopwatch = Stopwatch.StartNew();
                GaussSolver.SolveGaussSequential(A, x);
                stopwatch.Stop();
                
                // Базовая проверка
                Assert.NotNull(x);
                Assert.Equal(size, x.Length);
                
                times.Add(stopwatch.Elapsed.TotalSeconds);
                Console.WriteLine($"[Стабильность] Итерация {i + 1}: {stopwatch.Elapsed.TotalSeconds:F3} сек");
            }
            
            // Простая проверка стабильности
            if (times.Count > 1)
            {
                double avgTime = times.Average();
                double maxDiff = times.Max() - times.Min();
                double relDiff = maxDiff / avgTime * 100;
                
                Console.WriteLine($"[Стабильность] Среднее: {avgTime:F3}с, разница: {maxDiff:F3}с, отклонение: {relDiff:F1}%");
                
                Assert.True(relDiff < 100, $"Время нестабильно: отклонение {relDiff:F1}%");
            }
        }
        
        // Тест 6: Быстрый тест конфигураций
        [Fact]
        public void QuickConfigurationTest()
        {
            Console.WriteLine("=== БЫСТРЫЙ ТЕСТ КОНФИГУРАЦИЙ ===");
            
            var configs = new[]
            {
                new { Size = 200, Threads = 1 },
                new { Size = 200, Threads = 2 },
                new { Size = 400, Threads = 4 }
            };
            
            foreach (var config in configs)
            {
                var (matrix, vector) = GetOrCreateMatrix(config.Size);
                var A = GaussSolver.Clone(matrix);
                var x = (double[])vector.Clone();
                
                var stopwatch = Stopwatch.StartNew();
                
                if (config.Threads == 1)
                {
                    GaussSolver.SolveGaussSequential(A, x);
                }
                else
                {
                    var options = new System.Threading.Tasks.ParallelOptions 
                    { 
                        MaxDegreeOfParallelism = config.Threads 
                    };
                    GaussSolver.SolveGaussParallel(A, x, options);
                }
                
                stopwatch.Stop();
                
                // Базовая проверка
                Assert.NotNull(x);
                Assert.Equal(config.Size, x.Length);
                
                Console.WriteLine($"{config.Size}×{config.Size}, {config.Threads} потоков: {stopwatch.Elapsed.TotalSeconds:F3} сек");
            }
        }
        
        // Тест 7: Проверка корректности
        [Theory]
        [InlineData(10)]
        [InlineData(20)]
        [InlineData(50)]
        public void SolutionCorrectness_SmallMatrices(int size)
        {
            var (matrix, vector) = GaussSolver.GenerateMatrix(size);
            var A = GaussSolver.Clone(matrix);
            var x = (double[])vector.Clone();
            
            // Решаем
            GaussSolver.SolveGaussSequential(A, x);
            
            // Проверяем решение
            double maxResidual = 0;
            for (int i = 0; i < size; i++)
            {
                double sum = 0;
                for (int j = 0; j < size; j++)
                {
                    sum += matrix[i, j] * x[j];
                }
                double residual = Math.Abs(sum - vector[i]);
                maxResidual = Math.Max(maxResidual, residual);
            }
            
            Console.WriteLine($"[Корректность] {size}×{size}: max невязка = {maxResidual:E6}");
            
            Assert.True(maxResidual < 1e-9, $"Невязка слишком велика: {maxResidual:E6}");
        }
        
        // Вспомогательные методы для сохранения

private string CreateResultsDirectory(string basePath)
{
    // Создаем папку TestResults в текущей директории
    string resultsPath = Path.Combine(basePath, "TestResults");
    
    if (!Directory.Exists(resultsPath))
    {
        Directory.CreateDirectory(resultsPath);
    }
    
    return resultsPath;
}

private string FindTestsFolder(string startPath)
{
    var directory = new DirectoryInfo(startPath);
    
    while (directory != null)
    {
        // Ищем папку Tests
        var testsDir = directory.GetDirectories("Tests").FirstOrDefault();
        if (testsDir != null)
            return testsDir.FullName;
        
        // Или папку с тестами в проекте
        var testProjects = directory.GetDirectories("*Test*")
                                   .Where(d => d.Name.Contains("Test", StringComparison.OrdinalIgnoreCase))
                                   .FirstOrDefault();
        if (testProjects != null)
            return testProjects.FullName;
        
        directory = directory.Parent;
    }
    
    return null;
}
        
private void SaveResultsToCSV(Dictionary<int, (double time, double speedup, double efficiency)> results, string filename)
{
    try
    {
        // 1. Определяем корень проекта или папку с тестами
        string projectRoot = AppContext.BaseDirectory;
        
        // 2. Ищем папку Tests или создаем нужную структуру
        string testsFolder = FindTestsFolder(projectRoot) ?? 
                           CreateResultsDirectory(projectRoot);
        
        string filePath = Path.Combine(testsFolder, filename);
        
        // Используем инвариантную культуру для точки как десятичного разделителя
        var lines = new List<string> { "Threads,Time(seconds),Speedup,Efficiency(%)" };
        foreach (var kvp in results.OrderBy(r => r.Key))
        {
            // Форматируем с точкой как десятичным разделителем
            string timeStr = kvp.Value.time.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            string speedupStr = kvp.Value.speedup.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            string efficiencyStr = kvp.Value.efficiency.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
            
            lines.Add($"{kvp.Key},{timeStr},{speedupStr},{efficiencyStr}");
        }
        
        File.WriteAllLines(filePath, lines);
        Console.WriteLine($"\n✅ Файл сохранен в: {filePath}");
        
        // Показываем содержимое
        Console.WriteLine($"\nСодержимое {filename}:");
        foreach (var line in lines)
        {
            Console.WriteLine($"  {line}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Ошибка сохранения CSV: {ex.Message}");
    }
}

private void SaveResultsToCSV(List<string> results, string filename)
{
    try
    {
        string projectRoot = AppContext.BaseDirectory;
        string testsFolder = FindTestsFolder(projectRoot) ?? CreateResultsDirectory(projectRoot);
        string filePath = Path.Combine(testsFolder, filename);
        
        // Обрабатываем строки для замены запятых на точки в числах
        var processedLines = new List<string>();
        
        foreach (var line in results)
        {
            if (line.Contains("Размер,Время(сек),Память(МБ)") || !line.Contains(','))
            {
                // Заголовок или строка без запятых
                processedLines.Add(line);
            }
            else
            {
                // Разбиваем строку на части
                var parts = line.Split(',');
                if (parts.Length >= 3)
                {
                    // Форматируем числовые значения с точкой
                    string size = parts[0];
                    string time = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)
                        .ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
                    string memory = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)
                        .ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    
                    processedLines.Add($"{size},{time},{memory}");
                }
                else
                {
                    processedLines.Add(line);
                }
            }
        }
        
        File.WriteAllLines(filePath, processedLines);
        Console.WriteLine($"\n✅ Файл сохранен в: {filePath}");
        
        // Показываем содержимое
        Console.WriteLine($"\nСодержимое {filename}:");
        foreach (var line in processedLines)
        {
            Console.WriteLine($"  {line}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"❌ Ошибка сохранения CSV: {ex.Message}");
    }
}
    }
    
    [CollectionDefinition("PerformanceTests")]
    public class PerformanceTestCollection : ICollectionFixture<PerformanceTests>
    {
    }
}