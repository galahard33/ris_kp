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
                    model.SequentialSolutionPreview = GaussSolver.GetSolutionPreview(x1, 10);
                    model.ParallelSolutionPreview = GaussSolver.GetSolutionPreview(x2, 10);
                    model.MatrixPreview = GaussSolver.GetMatrixPreview(loadedA, loadedB, 10);
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
                    if (j % 100 == 0 && j > 0)
                        Console.WriteLine($"[Контроллер] Отправлено {j}/{n} столбцов");
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
                    if (k % 10 == 0)
                        Console.WriteLine($"[Контроллер] Шаг {k}/{n-1}");
                    
                    // 6.1. Выбор главного элемента
                    int workerWithColK = k % p;
                    
                    await writers[workerWithColK].WriteLineAsync($"GET_COLUMN {k}");
                    
                    var columnK = new double[n];
                    string response = await readers[workerWithColK].ReadLineAsync();
                    if (!response.StartsWith($"COLUMN {k}"))
                        throw new Exception($"Ошибка получения столбца {k}: {response}");
                        
                    for (int i = 0; i < n; i++)
                        columnK[i] = double.Parse(await readers[workerWithColK].ReadLineAsync() ?? "0");

                    // Находим строку с максимальным элементом
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
                    }

                    // 6.3. Нормализация
                    double pivotValue = columnK[k];
                    if (Math.Abs(pivotValue) < 1e-12)
                        throw new Exception($"Матрица вырожденна на шаге {k}");

                    // 6.4. Подготовка пакетов исключения для каждого worker'а
                    // Сначала собираем все операции для каждого worker'а
                    var operationsPerWorker = new List<(int i, double factor)>[p];
                    for (int w = 0; w < p; w++)
                        operationsPerWorker[w] = new List<(int i, double factor)>();

                    for (int i = k + 1; i < n; i++)
                    {
                        double factor = columnK[i] / pivotValue;
                        // Определяем, какие столбцы нужно обновлять у каждого worker'а
                        for (int w = 0; w < p; w++)
                        {
                            // Worker w обновит строку i для всех своих столбцов
                            // Добавляем операцию для этого worker'а
                            operationsPerWorker[w].Add((i, factor));
                        }
                        
                        // Обновляем локальный вектор b
                        b_local[i] -= factor * b_local[k];
                    }

                    // 6.5. Отправляем пакеты операций каждому worker'у
                    for (int w = 0; w < p; w++)
                    {
                        if (operationsPerWorker[w].Count > 0)
                        {
                            // Отправляем команду ELIMINATE_BATCH
                            await writers[w].WriteLineAsync($"ELIMINATE_BATCH {k} {operationsPerWorker[w].Count}");
                            
                            // Отправляем все операции в пакете
                            foreach (var op in operationsPerWorker[w])
                            {
                                await writers[w].WriteLineAsync($"{op.i} {op.factor:R}");
                            }
                            
                            // Завершаем пакет
                            await writers[w].WriteLineAsync("END_BATCH");
                            
                            // Ждем подтверждения
                            string batchResponse = await readers[w].ReadLineAsync();
                            if (batchResponse != "OK")
                                throw new Exception($"Worker {w} ошибка ELIMINATE_BATCH: {batchResponse}");
                        }
                    }
                }

                // 7. Получаем треугольную матрицу от worker'ов
                Console.WriteLine($"[Контроллер] Получение треугольной матрицы от узлов...");
                var U = new double[n, n];
                
                for (int w = 0; w < p; w++)
                {
                    await writers[w].WriteLineAsync("GET_MATRIX");
                    
                    string line;
                    while ((line = await readers[w].ReadLineAsync()) != null)
                    {
                        if (line == "END_MATRIX") break;
                        
                        if (line.StartsWith("COL"))
                        {
                            int j = int.Parse(line.Split(' ')[1]);
                            for (int i = 0; i < n; i++)
                            {
                                string val = await readers[w].ReadLineAsync() ?? "0";
                                U[i, j] = double.Parse(val);
                            }
                        }
                    }
                }

                // 8. Обратный ход (локально)
                Console.WriteLine($"[Контроллер] Выполнение обратного хода...");
                solution = new double[n];
                for (int i = n - 1; i >= 0; i--)
                {
                    solution[i] = b_local[i];
                    for (int j = i + 1; j < n; j++)
                        solution[i] -= U[i, j] * solution[j];
                    solution[i] /= U[i, i];
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