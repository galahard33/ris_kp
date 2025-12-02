using Microsoft.AspNetCore.Http;

namespace GaussWebApp.Models
{
    public class GaussViewModel
    {
        public int MatrixSize { get; set; } = 1000;
        public int ThreadCount { get; set; } = 4;
        public IFormFile? UploadedFile { get; set; } 

        public double TimeSequential { get; set; }
        public double TimeParallel { get; set; }
        public double Speedup { get; set; }
        public double MaxError { get; set; }
        public string[]? SolutionPreview { get; set; }
        public string[]? MatrixPreview { get; set; }
        public bool HasResult { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public bool RequestDownload { get; set; }
    }
}