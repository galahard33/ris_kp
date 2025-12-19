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

            var _locks = new SemaphoreSlim[p];
            for (int i = 0; i < p; i++)
                _locks[i] = new SemaphoreSlim(1, 1); // 1 поток может войти

                async Task<string> SendCommandWithLock(int worker, StreamWriter writer, StreamReader reader, string command)
    {
        await _locks[worker].WaitAsync();
        try
        {
            await writer.WriteLineAsync(command);
            return await reader.ReadLineAsync();
        }
        finally
        {
            _locks[worker].Release();
        }
    }
    
    // Локальная функция для отправки столбца с блокировкой
    async Task SendColumnWithLock(int worker, int columnIndex)
    {
        await _locks[worker].WaitAsync();
        try
        {
            await writers[worker].WriteLineAsync($"COL {columnIndex}");
            for (int row = 0; row < n; row++)
                await writers[worker].WriteLineAsync(loadedA[row, columnIndex].ToString("R"));
        }
        finally
        {
            _locks[worker].Release();
        }
    }
    
    // Локальная функция для получения столбца с блокировкой
            async Task<double[]> GetColumnWithLock(int worker, int columnIndex)
            {
                await _locks[worker].WaitAsync();
                try
                {
                    await writers[worker].WriteLineAsync($"GET_COLUMN {columnIndex}");
                    string response = await readers[worker].ReadLineAsync();
                    
                    if (!response.StartsWith($"COLUMN {columnIndex}"))
                        throw new Exception($"Ошибка получения столбца {columnIndex}: {response}");
                        
                    var column = new double[n];
                    for (int i = 0; i < n; i++)
                        column[i] = double.Parse(await readers[worker].ReadLineAsync() ?? "0");
                        
                    return column;
                }
                finally
                {
                    _locks[worker].Release();
                }
            }
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
                var columnTasks = new List<Task>();
                for (int j = 0; j < n; j++)
                {
                    int columnIndex = j; // capture для замыкания
                    int nodeIdx = columnIndex % p;
                    
                    columnTasks.Add(Task.Run(async () =>
                    {
                        await _locks[nodeIdx].WaitAsync();
                        try
                        {
                            await writers[nodeIdx].WriteLineAsync($"COL {columnIndex}");
                            for (int row = 0; row < n; row++)
                                await writers[nodeIdx].WriteLineAsync(loadedA[row, columnIndex].ToString("R"));
                        }
                        finally
                        {
                            _locks[nodeIdx].Release();
                        }
                        
                        // Логирование прогресса
                        if (columnIndex % 100 == 0)
                            Console.WriteLine($"[Контроллер] Отправлен столбец {columnIndex}/{n}");
                    }));
                    
                    // Ограничиваем число одновременно выполняемых задач
                    if (columnTasks.Count >= Environment.ProcessorCount * 2)
                    {
                        await Task.WhenAll(columnTasks);
                        columnTasks.Clear();
                    }
                }

                // Дожидаемся оставшихся задач
                if (columnTasks.Count > 0)
                    await Task.WhenAll(columnTasks);

                // 4. Отправляем вектор b на КАЖДЫЙ узел
                    Console.WriteLine($"[Контроллер] Параллельная отправка вектора b на {p} узлов...");

                    var vectorTasks = new List<Task>();
                    for (int workerIndex = 0; workerIndex < p; workerIndex++)
                    {
                        int worker = workerIndex; // capture для замыкания
                        
                        vectorTasks.Add(Task.Run(async () =>
                        {
                            await _locks[worker].WaitAsync();
                            try
                            {
                                await writers[worker].WriteLineAsync("VECTOR_B");
                                for (int row = 0; row < n; row++)
                                    await writers[worker].WriteLineAsync(loadedB[row].ToString("R"));
                            }
                            finally
                            {
                                _locks[worker].Release();
                            }
                        }));
                    }

                    await Task.WhenAll(vectorTasks);

                // 5. Ждём READY от всех
                Console.WriteLine($"[Контроллер] Ожидание готовности узлов...");
                for (int i = 0; i < p; i++)
                {
                    string response = await readers[i].ReadLineAsync();
                    if (response != "READY")
                        throw new Exception($"Worker {i} не готов: {response}");
                }

               Console.WriteLine($"[Контроллер] Начало прямого хода...");
var b_local = (double[])loadedB.Clone();
var localColumnK = new double[n]; // Храним столбец k локально для всех воркеров

for (int k = 0; k < n - 1; k++)
{
    // 6.1. Выбор главного элемента
    int workerWithColK = k % p;
    
    await writers[workerWithColK].WriteLineAsync($"GET_COLUMN {k}");
    
    string response = await readers[workerWithColK].ReadLineAsync();
    if (!response.StartsWith($"COLUMN {k}"))
        throw new Exception($"Ошибка получения столбца {k}: {response}");
        
    for (int i = 0; i < n; i++)
        localColumnK[i] = double.Parse(await readers[workerWithColK].ReadLineAsync() ?? "0");

    // Находим строку с максимальным элементом в столбце k
    int pivot = k;
    for (int i = k + 1; i < n; i++)
        if (Math.Abs(localColumnK[i]) > Math.Abs(localColumnK[pivot]))
            pivot = i;

    // 6.2. Обмен строками если нужно
    if (pivot != k)
    {
        Console.WriteLine($"[Контроллер] Обмен строк {k}<->{pivot} (макс элемент: {Math.Abs(localColumnK[pivot]):E6})");
        
            var swapTasks = new List<Task<string>>();
            for (int w = 0; w < p; w++)
            {
                int worker = w; // capture
                
                swapTasks.Add(Task.Run(async () =>
                {
                    await _locks[worker].WaitAsync();
                    try
                    {
                        await writers[worker].WriteLineAsync($"SWAP_ROWS {k} {pivot}");
                        return await readers[worker].ReadLineAsync();
                    }
                    finally
                    {
                        _locks[worker].Release();
                    }
                }));
            }

            var swapResults = await Task.WhenAll(swapTasks);
            for (int w = 0; w < p; w++)
            {
                if (swapResults[w] != "OK")
                    throw new Exception($"Worker {w} ошибка SWAP_ROWS: {swapResults[w]}");
            }
        
        // Обмен в локальном векторе b
        (b_local[k], b_local[pivot]) = (b_local[pivot], b_local[k]);
        
        // Обновляем localColumnK после обмена строк
        await writers[workerWithColK].WriteLineAsync($"GET_COLUMN {k}");
        response = await readers[workerWithColK].ReadLineAsync();
        for (int i = 0; i < n; i++)
            localColumnK[i] = double.Parse(await readers[workerWithColK].ReadLineAsync() ?? "0");
    }

    // 6.3. Нормализация строки k
    double pivotValue = localColumnK[k];
    if (Math.Abs(pivotValue) < 1e-12)
        throw new Exception($"Матрица вырожденна на шаге {k} (pivot={pivotValue:E6})");
    
    Console.WriteLine($"[Контроллер] Шаг {k}: делим строку {k} на {pivotValue:E6}");
    
    // Сначала обновляем локальный вектор b
    b_local[k] /= pivotValue;
    
    // Отправляем команды на нормализацию всем воркерам
        var normalizeTasks = new List<Task<(string, string)>>();
        for (int w = 0; w < p; w++)
        {
            int worker = w; // capture
            
            normalizeTasks.Add(Task.Run(async () =>
            {
                await _locks[worker].WaitAsync();
                try
                {
                    // Отправляем обе команды подряд для минимизации блокировок
                    await writers[worker].WriteLineAsync($"NORMALIZE_ROW {k} {pivotValue}");
                    await writers[worker].WriteLineAsync($"UPDATE_B_ELEMENT {k} {b_local[k]}");
                    
                    // Читаем оба ответа
                    string response1 = await readers[worker].ReadLineAsync();
                    string response2 = await readers[worker].ReadLineAsync();
                    
                    return (response1, response2);
                }
                finally
                {
                    _locks[worker].Release();
                }
            }));
        }

        var normalizeResults = await Task.WhenAll(normalizeTasks);
        for (int w = 0; w < p; w++)
        {
            if (normalizeResults[w].Item1 != "OK") 
                throw new Exception($"Worker {w} ошибка NORMALIZE_ROW: {normalizeResults[w].Item1}");
            if (normalizeResults[w].Item2 != "OK")
                throw new Exception($"Worker {w} ошибка UPDATE_B_ELEMENT: {normalizeResults[w].Item2}");
        }

    // 6.4. Исключение элементов ниже диагонали
    // Сначала вычисляем все multipliers для строк i > k
    var multipliers = new Dictionary<int, double>();
    for (int i = k + 1; i < n; i++)
    {
        multipliers[i] = localColumnK[i]; // factor = A[i,k]
    }
    
    // Обновляем локальный вектор b для всех строк i > k
    for (int i = k + 1; i < n; i++)
    {
        b_local[i] -= multipliers[i] * b_local[k];
    }
    
    // Теперь отправляем операции исключения для матрицы A каждому воркеру
    // Каждый воркер должен обновить ВСЕ свои столбцы для ВСЕХ строк i > k
    
    // Разделяем операции по воркерам для эффективности
var eliminateTasks = new List<Task<string>>();
for (int w = 0; w < p; w++)
{
    int worker = w; // capture
    
    eliminateTasks.Add(Task.Run(async () =>
    {
        // Подготавливаем операции для этого воркера
        var operations = new StringBuilder();
        for (int i = k + 1; i < n; i++)
        {
            operations.AppendLine($"{i} {multipliers[i]}");
        }
        
        if (operations.Length > 0)
        {
            await _locks[worker].WaitAsync();
            try
            {
                // Отправляем всё одним запросом
                await writers[worker].WriteLineAsync($"ELIMINATE_BATCH {k} {n - k - 1}");
                await writers[worker].WriteAsync(operations.ToString());
                await writers[worker].WriteLineAsync("END_BATCH");
                await writers[worker].FlushAsync();
                
                return await readers[worker].ReadLineAsync();
            }
            finally
            {
                _locks[worker].Release();
            }
        }
        return "OK"; // Нет операций - нет ошибок
    }));
}

var eliminateResults = await Task.WhenAll(eliminateTasks);
for (int w = 0; w < p; w++)
{
    if (eliminateResults[w] != "OK")
        throw new Exception($"Worker {w} ошибка ELIMINATE_BATCH: {eliminateResults[w]}");
}
    
    // Логируем прогресс
    if (k % 100 == 0 || k == n - 2)
    {
        Console.WriteLine($"[Контроллер] Выполнен шаг {k+1}/{n-1} ({(k+1)*100.0/(n-1):F1}%)");
    }
}

// 6.5. Нормализация последней диагонали (строки n-1)
int lastRow = n - 1;
int lastWorker = lastRow % p;

// Получаем последний диагональный элемент
await writers[lastWorker].WriteLineAsync($"GET_ELEMENT {lastRow} {lastRow}");
string lastDiagStr = await readers[lastWorker].ReadLineAsync();
double lastDiag = double.Parse(lastDiagStr);

if (Math.Abs(lastDiag) < 1e-12)
    throw new Exception($"Матрица вырожденна в последней строке (диагональ={lastDiag:E6})");

// Нормализуем последнюю строку
b_local[lastRow] /= lastDiag;

for (int w = 0; w < p; w++)
{
    await writers[w].WriteLineAsync($"NORMALIZE_ROW {lastRow} {lastDiag}");
    string response1 = await readers[w].ReadLineAsync();
    
    await writers[w].WriteLineAsync($"UPDATE_B_ELEMENT {lastRow} {b_local[lastRow]}");
    string response2 = await readers[w].ReadLineAsync();
    
    if (response1 != "OK") throw new Exception($"Worker {w} ошибка NORMALIZE_ROW для последней строки");
    if (response2 != "OK") throw new Exception($"Worker {w} ошибка UPDATE_B_ELEMENT для последней строки");
}

Console.WriteLine($"[Контроллер] Прямой ход завершен");

    // 7. После прямого хода все воркеры должны иметь верхнюю треугольную матрицу
    // Получаем диагональные элементы для обратного хода
var diag = new double[n];
var diagTasks = new List<Task<(int, double)>>();

for (int k = 0; k < n; k++)
{
    int row = k; // capture
    int workerIdx = row % p;
    
    diagTasks.Add(Task.Run(async () =>
    {
        await _locks[workerIdx].WaitAsync();
        try
        {
            await writers[workerIdx].WriteLineAsync($"GET_ELEMENT {row} {row}");
            string elem = await readers[workerIdx].ReadLineAsync();
            return (row, double.Parse(elem));
        }
        finally
        {
            _locks[workerIdx].Release();
        }
    }));
    
    // Батчинг для больших матриц
    if (diagTasks.Count >= 100)
    {
        var results = await Task.WhenAll(diagTasks);
        foreach (var (index, value) in results)
            diag[index] = value;
        diagTasks.Clear();
    }
}

// Оставшиеся задачи
if (diagTasks.Count > 0)
{
    var results = await Task.WhenAll(diagTasks);
    foreach (var (index, value) in results)
        diag[index] = value;
}
    // 8. Обратный ход
    Console.WriteLine($"[Контроллер] Выполнение обратного хода...");
    solution = new double[n];
    
for (int i = n - 1; i >= 0; i--)
{
    solution[i] = b_local[i];
    
    // Параллельно получаем все элементы строки i
    var rowTasks = new List<Task<(int, double)>>();
    for (int j = i + 1; j < n; j++)
    {
        int column = j; // capture
        int workerIdx = column % p;
        
        rowTasks.Add(Task.Run(async () =>
        {
            await _locks[workerIdx].WaitAsync();
            try
            {
                await writers[workerIdx].WriteLineAsync($"GET_ELEMENT {i} {column}");
                string elem = await readers[workerIdx].ReadLineAsync();
                return (column, double.Parse(elem));
            }
            finally
            {
                _locks[workerIdx].Release();
            }
        }));
    }
    
    // Обрабатываем батчами по 50 элементов
    var batchSize = 50;
    for (int batchStart = 0; batchStart < rowTasks.Count; batchStart += batchSize)
    {
        int batchEnd = Math.Min(batchStart + batchSize, rowTasks.Count);
        var batchTasks = rowTasks.GetRange(batchStart, batchEnd - batchStart);
        
        var batchResults = await Task.WhenAll(batchTasks);
        foreach (var (j, a_ij) in batchResults)
        {
            solution[i] -= a_ij * solution[j];
        }
    }
    
    solution[i] /= diag[i];
    
    // Прогресс для больших матриц
    if (i % 100 == 0)
        Console.WriteLine($"[Контроллер] Обратный ход: обработано {n - i}/{n}");
}


                // 9. Завершаем worker'ов
                Console.WriteLine($"[Контроллер] Завершение работы узлов...");
var doneTasks = new List<Task>();
for (int i = 0; i < p; i++)
{
    int worker = i; // capture
    
    doneTasks.Add(Task.Run(async () =>
    {
        await _locks[worker].WaitAsync();
        try
        {
            await writers[worker].WriteLineAsync("DONE");
        }
        finally
        {
            _locks[worker].Release();
        }
    }));
}

await Task.WhenAll(doneTasks);

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
                    _locks[i]?.Dispose();
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