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
        // 1. Параллельное подключение
        Console.WriteLine($"[Контроллер] Подключение к {p} узлам...");
        var connectTasks = new Task[p];
        for (int i = 0; i < p; i++)
        {
            int idx = i;
            connectTasks[i] = Task.Run(async () =>
            {
                clients[idx] = new TcpClient();
                clients[idx].ReceiveTimeout = 30000;
                clients[idx].SendTimeout = 30000;
                await clients[idx].ConnectAsync(nodes[idx].Host, nodes[idx].Port);
                var stream = clients[idx].GetStream();
                writers[idx] = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                readers[idx] = new StreamReader(stream, Encoding.UTF8);
                Console.WriteLine($"[Контроллер] Подключен к узлу {idx}: {nodes[idx].Host}:{nodes[idx].Port}");
            });
        }
        await Task.WhenAll(connectTasks);

        // 2. Инициализация - ПАРАЛЛЕЛЬНО
        Console.WriteLine($"[Контроллер] Инициализация узлов с n={n}");
        var initTasks = new Task[p];
        for (int i = 0; i < p; i++)
        {
            int idx = i;
            initTasks[i] = writers[idx].WriteLineAsync($"INIT {n}");
        }
        await Task.WhenAll(initTasks);

        // 3. Пакетная отправка столбцов
        Console.WriteLine($"[Контроллер] Пакетная отправка столбцов...");
        
        // Готовим данные для каждого узла
        var sendTasks = new Task[p];
        for (int nodeIdx = 0; nodeIdx < p; nodeIdx++)
        {
            int currentNode = nodeIdx;
            sendTasks[currentNode] = Task.Run(async () =>
            {
                var batchBuilder = new StringBuilder();
                
                for (int j = currentNode; j < n; j += p)
                {
                    batchBuilder.AppendLine($"COL {j}");
                    for (int row = 0; row < n; row++)
                    {
                        batchBuilder.AppendLine(loadedA[row, j].ToString("R"));
                    }
                    
                    // Отправляем пачками по 5 столбцов
                    if (batchBuilder.Length > 50000)
                    {
                        await writers[currentNode].WriteAsync(batchBuilder.ToString());
                        batchBuilder.Clear();
                    }
                }
                
                if (batchBuilder.Length > 0)
                {
                    await writers[currentNode].WriteAsync(batchBuilder.ToString());
                }
                
                int columnsSent = (n - currentNode + p - 1) / p;
                Console.WriteLine($"[Контроллер] Узел {currentNode}: отправлено {columnsSent} столбцов");
            });
        }
        await Task.WhenAll(sendTasks);

        // 4. Отправка вектора b - ПАРАЛЛЕЛЬНО
        Console.WriteLine($"[Контроллер] Отправка вектора b...");
        var vectorBuilder = new StringBuilder();
        vectorBuilder.AppendLine("VECTOR_B");
        for (int row = 0; row < n; row++)
        {
            vectorBuilder.AppendLine(loadedB[row].ToString("R"));
        }
        string vectorData = vectorBuilder.ToString();
        
        var vectorTasks = new Task[p];
        for (int i = 0; i < p; i++)
        {
            int idx = i;
            vectorTasks[i] = writers[idx].WriteAsync(vectorData);
        }
        await Task.WhenAll(vectorTasks);

        // 5. Проверка готовности - ПАРАЛЛЕЛЬНО
        Console.WriteLine($"[Контроллер] Ожидание готовности узлов...");
        var readyTasks = new Task<string>[p];
        for (int i = 0; i < p; i++)
        {
            int idx = i;
            readyTasks[i] = readers[idx].ReadLineAsync();
        }
        
        var responses = await Task.WhenAll(readyTasks);
        for (int i = 0; i < p; i++)
        {
            if (responses[i] != "READY")
                throw new Exception($"Worker {i} не готов: {responses[i]}");
        }

        // 6. Прямой ход с оптимизациями
        Console.WriteLine($"[Контроллер] Начало прямого хода...");
        var b_local = (double[])loadedB.Clone();
        
        // Кэш для столбцов
        var columnCache = new Dictionary<int, double[]>();
        
        for (int k = 0; k < n - 1; k++)
        {
            int workerWithColK = k % p;
            
            // Получаем столбец k (с кэшированием)
            double[] columnK;
            if (!columnCache.TryGetValue(k, out columnK))
            {
                await writers[workerWithColK].WriteLineAsync($"GET_COLUMN {k}");
                
                string response = await readers[workerWithColK].ReadLineAsync();
                if (!response.StartsWith($"COLUMN {k}"))
                    throw new Exception($"Ошибка получения столбца {k}");
                    
                columnK = new double[n];
                
                // Параллельное чтение элементов столбца
                var readTasks = new Task<string>[n];
                for (int i = 0; i < n; i++)
                {
                    readTasks[i] = readers[workerWithColK].ReadLineAsync();
                }
                
                var columnValues = await Task.WhenAll(readTasks);
                for (int i = 0; i < n; i++)
                {
                    columnK[i] = double.Parse(columnValues[i] ?? "0");
                }
                
                columnCache[k] = columnK;
            }

            // Выбор главного элемента
            int pivot = k;
            for (int i = k + 1; i < n; i++)
                if (Math.Abs(columnK[i]) > Math.Abs(columnK[pivot]))
                    pivot = i;

            // Обмен строками если нужно
            if (pivot != k)
            {
                // Параллельная отправка команд обмена
                var swapTasks = new Task[p];
                for (int w = 0; w < p; w++)
                {
                    int workerIdx = w;
                    swapTasks[w] = writers[workerIdx].WriteLineAsync($"SWAP_ROWS {k} {pivot}");
                }
                await Task.WhenAll(swapTasks);
                
                // Параллельная проверка ответов
                var swapResponseTasks = new Task<string>[p];
                for (int w = 0; w < p; w++)
                {
                    int workerIdx = w;
                    swapResponseTasks[w] = readers[workerIdx].ReadLineAsync();
                }
                
                var swapResponses = await Task.WhenAll(swapResponseTasks);
                for (int w = 0; w < p; w++)
                {
                    if (swapResponses[w] != "OK")
                        throw new Exception($"Worker {w} ошибка SWAP_ROWS");
                }
                
                (b_local[k], b_local[pivot]) = (b_local[pivot], b_local[k]);
                (columnK[k], columnK[pivot]) = (columnK[pivot], columnK[k]);
            }

            // Нормализация
            double pivotValue = columnK[k];
            if (Math.Abs(pivotValue) < 1e-12)
                throw new Exception($"Матрица вырожденна на шаге {k}");
            
            b_local[k] /= pivotValue;
            
            // Параллельная отправка нормализации
            var normalizeTasks = new Task[p];
            for (int w = 0; w < p; w++)
            {
                int workerIdx = w;
                normalizeTasks[w] = writers[workerIdx].WriteLineAsync($"NORMALIZE_ROW {k} {pivotValue}");
            }
            await Task.WhenAll(normalizeTasks);
            
            var normResponseTasks = new Task<string>[p];
            for (int w = 0; w < p; w++)
            {
                int workerIdx = w;
                normResponseTasks[w] = readers[workerIdx].ReadLineAsync();
            }
            
            var normResponses = await Task.WhenAll(normResponseTasks);
            for (int w = 0; w < p; w++)
            {
                if (normResponses[w] != "OK")
                    throw new Exception($"Worker {w} ошибка NORMALIZE_ROW");
            }

            // Исключение - пакетная отправка
            int operationsCount = n - k - 1;
            if (operationsCount > 0)
            {
                var operationsBuilder = new StringBuilder();
                operationsBuilder.AppendLine($"ELIMINATE_BATCH {k} {operationsCount}");
                
                for (int i = k + 1; i < n; i++)
                {
                    operationsBuilder.AppendLine($"{i} {columnK[i]}");
                }
                operationsBuilder.AppendLine("END_BATCH");
                
                await writers[workerWithColK].WriteAsync(operationsBuilder.ToString());
                
                string elimResponse = await readers[workerWithColK].ReadLineAsync();
                if (elimResponse != "OK")
                    throw new Exception($"Worker {workerWithColK} ошибка ELIMINATE_BATCH");
            }

            // Обновляем b_local
            for (int i = k + 1; i < n; i++)
            {
                b_local[i] -= columnK[i] * b_local[k];
            }
            
            // Очистка старого кэша
            if (columnCache.Count > 20)
            {
                var oldKeys = columnCache.Keys.Where(key => key < k - 10).ToList();
                foreach (var key in oldKeys) columnCache.Remove(key);
            }
            
            if ((k + 1) % 100 == 0 || k == n - 2)
            {
                Console.WriteLine($"[Контроллер] Обработан шаг {k + 1}/{n - 1}");
            }
        }

        // 7. Получение диагональных элементов с использованием GET_MULTIPLE_ELEMENTS
        Console.WriteLine($"[Контроллер] Получение диагональных элементов...");
        var diag = new double[n];
        
        // Группируем запросы по узлам
        var diagRequestsByNode = new Dictionary<int, List<int>>();
        for (int k = 0; k < n; k++)
        {
            int workerIdx = k % p;
            if (!diagRequestsByNode.ContainsKey(workerIdx))
                diagRequestsByNode[workerIdx] = new List<int>();
            diagRequestsByNode[workerIdx].Add(k);
        }
        
        // Отправляем запросы пакетами
        var diagTasks = new List<Task>();
        foreach (var kvp in diagRequestsByNode)
        {
            int workerIdx = kvp.Key;
            var columns = kvp.Value;
            
            diagTasks.Add(Task.Run(async () =>
            {
                var requestBuilder = new StringBuilder();
                requestBuilder.AppendLine("GET_MULTIPLE_ELEMENTS");
                requestBuilder.AppendLine(columns.Count.ToString());
                
                foreach (int col in columns)
                {
                    requestBuilder.AppendLine($"{col} {col}");
                }
                
                await writers[workerIdx].WriteAsync(requestBuilder.ToString());
                
                // Читаем ответы
                for (int i = 0; i < columns.Count; i++)
                {
                    string elem = await readers[workerIdx].ReadLineAsync();
                    diag[columns[i]] = double.Parse(elem);
                }
            }));
        }
        await Task.WhenAll(diagTasks);

        // 8. Оптимизированный обратный ход
        Console.WriteLine($"[Контроллер] Выполнение обратного хода...");
        solution = new double[n];
        
        const int BATCH_SIZE = 20; // Обрабатываем по 20 строк за раз
        
        for (int batchStart = n - 1; batchStart >= 0; batchStart -= BATCH_SIZE)
        {
            int batchEnd = Math.Max(batchStart - BATCH_SIZE, -1);
            
            for (int i = batchStart; i > batchEnd; i--)
            {
                solution[i] = b_local[i];
                
                // Группируем запросы по worker'ам
                var requestsByWorker = new Dictionary<int, List<int>>();
                for (int j = i + 1; j < n; j++)
                {
                    int workerIdx = j % p;
                    if (!requestsByWorker.ContainsKey(workerIdx))
                        requestsByWorker[workerIdx] = new List<int>();
                    requestsByWorker[workerIdx].Add(j);
                }
                
                // Выполняем запросы параллельно
                var sumTasks = new List<Task<double>>();
                foreach (var kvp in requestsByWorker)
                {
                    int workerIdx = kvp.Key;
                    var columns = kvp.Value;
                    
                    sumTasks.Add(Task.Run(async () =>
                    {
                        double sum = 0;
                        
                        // Используем GET_MULTIPLE_ELEMENTS для пакетного запроса
                        var requestBuilder = new StringBuilder();
                        requestBuilder.AppendLine("GET_MULTIPLE_ELEMENTS");
                        requestBuilder.AppendLine(columns.Count.ToString());
                        
                        foreach (int col in columns)
                        {
                            requestBuilder.AppendLine($"{i} {col}");
                        }
                        
                        await writers[workerIdx].WriteAsync(requestBuilder.ToString());
                        
                        // Читаем ответы
                        foreach (int col in columns)
                        {
                            string elem = await readers[workerIdx].ReadLineAsync();
                            double a_ij = double.Parse(elem);
                            sum += a_ij * solution[col];
                        }
                        
                        return sum;
                    }));
                }
                
                var sumResults = await Task.WhenAll(sumTasks);
                solution[i] -= sumResults.Sum();
                solution[i] /= diag[i];
            }
            
            int processed = n - batchStart + Math.Min(BATCH_SIZE, batchStart + 1);
            Console.WriteLine($"[Контроллер] Обратный ход: {processed}/{n} строк");
        }

        // 9. Завершение
        Console.WriteLine($"[Контроллер] Завершение работы узлов...");
        var doneTasks = new Task[p];
        for (int i = 0; i < p; i++)
        {
            int idx = i;
            doneTasks[i] = writers[idx].WriteLineAsync("DONE");
        }
        await Task.WhenAll(doneTasks);

        sw.Stop();
        
        // 10. Проверка
        Console.WriteLine($"[Контроллер] Проверка решения...");
        double maxResidual = 0;
        Parallel.For(0, Math.Min(10, n), i =>
        {
            double sum = 0;
            for (int j = 0; j < n; j++)
                sum += loadedA[i, j] * solution[j];
            double residual = Math.Abs(sum - loadedB[i]);
            if (residual > maxResidual)
                maxResidual = residual;
            
            if (i < 3)
                Console.WriteLine($"[Контроллер] x[{i}] = {solution[i]:F6}, невязка = {residual:E6}");
        });
        
        Console.WriteLine($"[Контроллер] === Распределённое вычисление завершено ===");
        Console.WriteLine($"[Контроллер] Узлов: {nodeCount}, Время: {sw.Elapsed.TotalSeconds:F3}с");
        Console.WriteLine($"[Контроллер] Макс. невязка: {maxResidual:E10}");
    }
    finally
    {
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