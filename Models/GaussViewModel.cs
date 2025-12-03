using Microsoft.AspNetCore.Http;

namespace GaussWebApp.Models
{
    public class GaussViewModel
    {
        // Основные параметры
        public int MatrixSize { get; set; } = 1000;
        public IFormFile? UploadedFile { get; set; }
        
        // Выбор метода вычислений
        public CalculationMethod CalculationMethod { get; set; } = CalculationMethod.Parallel;
        public int ThreadCount { get; set; } = 4; // Для параллельного метода
        
        // Результаты
        public double TimeSequential { get; set; }
        public double TimeParallel { get; set; }
        public double Speedup { get; set; }
        public double MaxError { get; set; }
        
        // Предпросмотры
        public string[]? MatrixPreview { get; set; }
        public string[]? SequentialSolutionPreview { get; set; }
        public string[]? ParallelSolutionPreview { get; set; }
        
        // Статус
        public bool HasResult { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public bool RequestDownload { get; set; }
        
        // Информация о распределенном вычислении
        public int NodeCount { get; set; }
        public string? NodeInfo { get; set; }
    }

    public enum CalculationMethod
    {
        Parallel,      // Многопоточное на одном компьютере
        Distributed    // Распределенное по нескольким узлам
    }
}