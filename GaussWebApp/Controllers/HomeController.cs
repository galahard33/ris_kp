using Microsoft.AspNetCore.Mvc;
using GaussWebApp.Models;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Threading;

namespace GaussWebApp.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index() => View(new GaussViewModel());

        [HttpPost]
        public async Task<IActionResult> Index(GaussViewModel model)
        {
            // ========== Валидация и загрузка данных ==========
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

            // Загрузка или генерация матрицы
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
                // ========== 1. Однопоточное решение (для сравнения) ==========
                var A1 = GaussSolver.Clone(loadedA);
                var x1 = (double[])loadedB.Clone();
                var sw1 = System.Diagnostics.Stopwatch.StartNew();
                GaussSolver.SolveGaussSequential(A1, x1);
                sw1.Stop();
                double t1 = sw1.Elapsed.TotalSeconds;

                // ========== 2. Выбранный метод решения ==========
                double t2 = 0;
                double[] x2 = null;

                if (model.CalculationMethod == CalculationMethod.Parallel)
                {
                    // Параллельный метод на одном компьютере
                    (x2, t2) = await SolveParallelAsync(loadedA, loadedB, model.ThreadCount);
                    model.NodeInfo = $"Локально, {model.ThreadCount} поток(а)";
                }
                else
                {
                    // Распределенный метод
                    (x2, t2, model.NodeCount, model.NodeInfo) = await SolveDistributedAsync(loadedA, loadedB);
                    if (x2 == null)
                    {
                        // Ошибка уже установлена в SolveDistributedAsync
                        return View(model);
                    }
                }

                // ========== 3. Результаты ==========
                if (x2 != null)
                {
                    model.TimeSequential = t1;
                    model.TimeParallel = t2;
                    model.Speedup = t1 / t2;
                    model.MaxError = GaussSolver.ComputeMaxError(x1, x2);
                    model.SequentialSolutionPreview = GaussSolver.GetSolutionPreview(x1, 20);
                    model.ParallelSolutionPreview = GaussSolver.GetSolutionPreview(x2, 20);
                    model.MatrixPreview = GaussSolver.GetMatrixPreview(loadedA, loadedB, 20);
                    model.HasResult = true;
                }
            }
            catch (Exception ex)
            {
                model.ErrorMessage = $"Ошибка вычислений: {ex.Message}";
            }

            return View(model);
        }

        private async Task<(double[] solution, double time)> SolveParallelAsync(double[,] A, double[] b, int threadCount)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            var A2 = GaussSolver.Clone(A);
            var x2 = (double[])b.Clone();
            var opts = new ParallelOptions { MaxDegreeOfParallelism = threadCount };
            
            await Task.Run(() => GaussSolver.SolveGaussParallel(A2, x2, opts));
            
            sw.Stop();
            return (x2, sw.Elapsed.TotalSeconds);
        }

        private async Task<(double[] solution, double time, int nodeCount, string nodeInfo)> 
            SolveDistributedAsync(double[,] loadedA, double[] loadedB)
        {
            if (!System.IO.File.Exists("nodes.txt"))
            {
                throw new FileNotFoundException("Файл nodes.txt не найден.");
            }

            var lines = await System.IO.File.ReadAllLinesAsync("nodes.txt");
            var nodes = new List<(string Host, int Port)>();
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line) || line.Trim().StartsWith("#")) 
                    continue;
                    
                var parts = line.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out int port))
                {
                    nodes.Add((parts[0].Trim(), port));
                }
            }

            if (nodes.Count == 0)
            {
                throw new InvalidOperationException("Файл nodes.txt не содержит корректных узлов.");
            }

            var nodeCount = nodes.Count;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            
            int n = loadedB.Length;
            int p = nodes.Count;

            // Подключение к worker'ам
            var clients = new TcpClient[p];
            var writers = new StreamWriter[p];
            var readers = new StreamReader[p];
            
            double[] solution = null;

            try
            {
                // 1. Подключаемся ко всем worker'ам
                for (int i = 0; i < p; i++)
                {
                    clients[i] = new TcpClient();
                    clients[i].Connect(nodes[i].Host, nodes[i].Port);
                    var stream = clients[i].GetStream();
                    writers[i] = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                    readers[i] = new StreamReader(stream, Encoding.UTF8);
                    Console.WriteLine($"[Контроллер] Подключен к узлу {i}: {nodes[i].Host}:{nodes[i].Port}");
                }

                // 2. Инициализация
                Console.WriteLine($"[Контроллер] Инициализация узлов с n={n}");
                for (int i = 0; i < p; i++)
                    await writers[i].WriteLineAsync($"INIT {n}");

                // 3. Циклическое размещение столбцов матрицы A
                Console.WriteLine($"[Контроллер] Распределение столбцов между {p} узлами...");
                for (int j = 0; j < n; j++)
                {
                    int nodeIdx = j % p;
                    await writers[nodeIdx].WriteLineAsync($"COL {j}");
                    for (int row = 0; row < n; row++)
                        await writers[nodeIdx].WriteLineAsync(loadedA[row, j].ToString("R"));
                    
                    // Логирование прогресса
                    // if (j % 100 == 0 && j > 0)
                    //     Console.WriteLine($"[Контроллер] Отправлено {j}/{n} столбцов");
                }

                // 4. Отправляем вектор b на КАЖДЫЙ узел
                Console.WriteLine($"[Контроллер] Отправка вектора b...");
                for (int i = 0; i < p; i++)
                {
                    await writers[i].WriteLineAsync("VECTOR_B");
                    for (int row = 0; row < n; row++)
                        await writers[i].WriteLineAsync(loadedB[row].ToString("R"));
                }

                // 5. Ждём READY от всех
                Console.WriteLine($"[Контроллер] Ожидание готовности узлов...");
                for (int i = 0; i < p; i++)
                {
                    string response = await readers[i].ReadLineAsync();
                    if (response != "READY")
                        throw new Exception($"Worker {i} не готов: {response}");
                }

                // 6. Прямой ход метода Гаусса
                Console.WriteLine($"[Контроллер] Начало прямого хода...");
    var b_local = (double[])loadedB.Clone();
    
    for (int k = 0; k < n - 1; k++)
    {
        // 6.1. Выбор главного элемента
        int workerWithColK = k % p;
        
        await writers[workerWithColK].WriteLineAsync($"GET_COLUMN {k}");
        
        var columnK = new double[n];
        string response = await readers[workerWithColK].ReadLineAsync();
        if (!response.StartsWith($"COLUMN {k}"))
            throw new Exception($"Ошибка получения столбца {k}");
            
        for (int i = 0; i < n; i++)
            columnK[i] = double.Parse(await readers[workerWithColK].ReadLineAsync() ?? "0");

        // Находим строку с максимальным элементом в столбце k
        int pivot = k;
        for (int i = k + 1; i < n; i++)
            if (Math.Abs(columnK[i]) > Math.Abs(columnK[pivot]))
                pivot = i;

        // 6.2. Обмен строками если нужно
        if (pivot != k)
        {
            for (int w = 0; w < p; w++)
            {
                await writers[w].WriteLineAsync($"SWAP_ROWS {k} {pivot}");
                if (await readers[w].ReadLineAsync() != "OK")
                    throw new Exception($"Worker {w} ошибка SWAP_ROWS");
            }
            
            (b_local[k], b_local[pivot]) = (b_local[pivot], b_local[k]);
            
            // Нужно обновить columnK после обмена строк
            await writers[workerWithColK].WriteLineAsync($"GET_COLUMN {k}");
            response = await readers[workerWithColK].ReadLineAsync();
            for (int i = 0; i < n; i++)
                columnK[i] = double.Parse(await readers[workerWithColK].ReadLineAsync() ?? "0");
        }

        // 6.3. Нормализация - делим строку k на A[k,k]
        double pivotValue = columnK[k];
        if (Math.Abs(pivotValue) < 1e-12)
            throw new Exception($"Матрица вырожденна на шаге {k}");
        
        // Обновляем строку k в векторе b
        b_local[k] /= pivotValue;
        
        // Отправляем команду на нормализацию строки k всем воркерам
        for (int w = 0; w < p; w++)
        {
            await writers[w].WriteLineAsync($"NORMALIZE_ROW {k} {pivotValue}");
            if (await readers[w].ReadLineAsync() != "OK")
                throw new Exception($"Worker {w} ошибка NORMALIZE_ROW");
        }

        // 6.4. Исключение элементов ниже диагонали
        // Нужно собрать все операции исключения и отправить пакетом каждому воркеру
        for (int w = 0; w < p; w++)
        {
            // Для каждого воркера собираем только те строки, которые ему нужны
            // (те, у которых соответствующие столбцы хранятся на этом воркере)
            var operations = new List<string>();
            
            for (int i = k + 1; i < n; i++)
            {
                // Вычисляем множитель для строки i
                // Для этого нужно получить элемент A[i, k] у воркера, который хранит столбец k
                if (w == workerWithColK)
                {
                    // Этот воркер хранит столбец k, поэтому у него есть A[i, k]
                    double factor = columnK[i]; // Это A[i, k] после обмена строк
                    operations.Add($"{i} {factor}");
                }
            }
            
            if (operations.Count > 0)
            {
                await writers[w].WriteLineAsync($"ELIMINATE_BATCH {k} {operations.Count}");
                foreach (var op in operations)
                    await writers[w].WriteLineAsync(op);
                await writers[w].WriteLineAsync("END_BATCH");
                
                if (await readers[w].ReadLineAsync() != "OK")
                    throw new Exception($"Worker {w} ошибка ELIMINATE_BATCH");
            }
        }
        
        // Обновляем b_local для строк ниже k
        for (int i = k + 1; i < n; i++)
        {
            // Для этого нужно получить A[i, k] - должен быть сохранен где-то
            double factor = columnK[i];
            b_local[i] -= factor * b_local[k];
        }
    }

    // 7. После прямого хода все воркеры должны иметь верхнюю треугольную матрицу
    // Получаем диагональные элементы для обратного хода
    var diag = new double[n];
    for (int k = 0; k < n; k++)
    {
        int workerIdx = k % p;
        await writers[workerIdx].WriteLineAsync($"GET_ELEMENT {k} {k}");
        string elem = await readers[workerIdx].ReadLineAsync();
        diag[k] = double.Parse(elem);
    }

    // 8. Обратный ход
    Console.WriteLine($"[Контроллер] Выполнение обратного хода...");
    solution = new double[n];
    
    // Сначала выполняем обратный ход для верхней треугольной матрицы
    for (int i = n - 1; i >= 0; i--)
    {
        solution[i] = b_local[i];
        
        // Для каждого j > i нужно вычислить сумму A[i,j]*x[j]
        // A[i,j] распределены по разным воркерам
        for (int j = i + 1; j < n; j++)
        {
            int workerIdx = j % p;
            await writers[workerIdx].WriteLineAsync($"GET_ELEMENT {i} {j}");
            string elem = await readers[workerIdx].ReadLineAsync();
            double a_ij = double.Parse(elem);
            solution[i] -= a_ij * solution[j];
        }
        
        solution[i] /= diag[i];
    }


                // 9. Завершаем worker'ов
                Console.WriteLine($"[Контроллер] Завершение работы узлов...");
                for (int i = 0; i < p; i++)
                {
                    await writers[i].WriteLineAsync("DONE");
                }

                sw.Stop();
                
                // 10. Проверка корректности
                Console.WriteLine($"[Контроллер] Проверка решения...");
                double maxResidual = 0;
                for (int i = 0; i < Math.Min(10, n); i++)
                {
                    double sum = 0;
                    for (int j = 0; j < n; j++)
                        sum += loadedA[i, j] * solution[j];
                    double residual = Math.Abs(sum - loadedB[i]);
                    maxResidual = Math.Max(maxResidual, residual);
                    
                    if (i < 3)
                        Console.WriteLine($"[Контроллер] x[{i}] = {solution[i]:F6}, невязка = {residual:E6}");
                }
                
                Console.WriteLine($"[Контроллер] === Распределённое вычисление завершено ===");
                Console.WriteLine($"[Контроллер] Узлов: {nodeCount}, Время: {sw.Elapsed.TotalSeconds:F3}с");
                Console.WriteLine($"[Контроллер] Макс. невязка: {maxResidual:E10}");
            }
            finally
            {
                // Очистка ресурсов
                for (int i = 0; i < p; i++)
                {
                    writers[i]?.Dispose();
                    readers[i]?.Dispose();
                    clients[i]?.Close();
                }
            }

            string nodeInfo = $"{nodeCount} узел(ов): ";
            for (int i = 0; i < Math.Min(nodeCount, 3); i++)
            {
                nodeInfo += $"{nodes[i].Host}:{nodes[i].Port}";
                if (i < Math.Min(nodeCount, 3) - 1) nodeInfo += ", ";
            }
            if (nodeCount > 3) nodeInfo += "...";

            return (solution, sw.Elapsed.TotalSeconds, nodeCount, nodeInfo);
        }
    }
}