using Microsoft.AspNetCore.Mvc;
using GaussWebApp.Models;
using System.IO;

namespace GaussWebApp.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            return View(new GaussViewModel());
        }

        [HttpPost]
        public IActionResult Index(GaussViewModel model)
        {
             if (model.UploadedFile == null && Request.Form.Files.Count > 0)
    {
        model.ErrorMessage = "Файл слишком большой. Макс. размер: 100 МБ.";
        return View(model);
    }
            // Скачивание матрицы
            if (model.RequestDownload)
            {
                if (model.MatrixSize < 100 || model.MatrixSize > 5000)
                {
                    ModelState.AddModelError("MatrixSize", "Размер должен быть от 100 до 5000.");
                    return View(model);
                }

                var (matrixA, vectorB) = GaussSolver.GenerateMatrix(model.MatrixSize);
                var memory = MatrixIO.SaveMatrixToStream(matrixA, vectorB);
                return File(memory.ToArray(), "text/plain", $"matrix_{model.MatrixSize}.txt");
            }

            double[,] loadedA;
            double[] loadedB;

            if (model.UploadedFile != null && model.UploadedFile.Length > 0)
            {
                try
                {
                    (loadedA, loadedB) = MatrixIO.LoadMatrixFromStream(model.UploadedFile.OpenReadStream());
                    model.MatrixSize = loadedB.Length;
                }
                catch (Exception ex)
                {
                    model.ErrorMessage = $"Ошибка чтения файла: {ex.Message}";
                    return View(model);
                }
            }
            else
            {
                if (model.MatrixSize < 100 || model.MatrixSize > 5000)
                {
                    ModelState.AddModelError("MatrixSize", "Размер должен быть от 100 до 5000.");
                    return View(model);
                }
                (loadedA, loadedB) = GaussSolver.GenerateMatrix(model.MatrixSize);
            }

            try
            {
                var A1 = GaussSolver.Clone(loadedA);
                var x1 = (double[])loadedB.Clone();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                GaussSolver.SolveGaussSequential(A1, x1);
                sw.Stop();
                double t1 = sw.Elapsed.TotalSeconds;

                var A2 = GaussSolver.Clone(loadedA);
                var x2 = (double[])loadedB.Clone();
                var opts = new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = model.ThreadCount };
                sw.Restart();
                GaussSolver.SolveGaussParallel(A2, x2, opts);
                sw.Stop();
                double t2 = sw.Elapsed.TotalSeconds;

                model.TimeSequential = t1;
                model.TimeParallel = t2;
                model.Speedup = t1 / t2;
                model.MaxError = GaussSolver.ComputeMaxError(x1, x2);
                model.SolutionPreview = GaussSolver.GetSolutionPreview(x1, 10);
                model.MatrixPreview = GaussSolver.GetMatrixPreview(loadedA, loadedB, 10);
                model.HasResult = true;
            }
            catch (Exception ex)
            {
                model.ErrorMessage = $"Ошибка вычислений: {ex.Message}";
            }

            return View(model);
        }
    }
}