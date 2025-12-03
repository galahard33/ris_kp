using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Linq;

namespace GaussWorker;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Использование: GaussWorker <порт>");
            Console.WriteLine("Пример: GaussWorker 9001");
            return;
        }

        if (!int.TryParse(args[0], out int port))
        {
            Console.WriteLine("Ошибка: порт должен быть числом");
            return;
        }

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"[Worker {port}] Слушаю порт {port} (оптимизированная версия)");

        while (true)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClientAsync(client, port));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Worker {port}] Ошибка при принятии соединения: {ex.Message}");
            }
        }
    }

    static async Task HandleClientAsync(TcpClient client, int port)
    {
        // Используем массив вместо Dictionary для быстрого доступа по индексу
        double[][] localColumns = null;
        double[] b = null;
        int n = 0;
        
        // Кэш для часто запрашиваемых столбцов
        var columnCache = new Dictionary<int, double[]>();
        // Кэш для часто запрашиваемых элементов
        var elementCache = new Dictionary<(int, int), double>();

        try
        {
            using (client)
            {
                // Устанавливаем таймауты для избежания зависаний
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 30000;
                
                var stream = client.GetStream();
                // Увеличиваем буфер для пакетной обработки
                stream.ReadTimeout = 5000;
                
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 8192))
                using (var writer = new StreamWriter(stream, Encoding.UTF8, 8192) { AutoFlush = true })
                {
                    Console.WriteLine($"[Worker {port}] Подключение установлено");

                    // Буфер для пакетного чтения
                    var buffer = new StringBuilder();
                    string line;
                    
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        try
                        {
                            if (line.StartsWith("INIT"))
                            {
                                var parts = line.Split(' ');
                                if (parts.Length != 2)
                                {
                                    await writer.WriteLineAsync("ERROR: Неверный формат INIT");
                                    continue;
                                }

                                n = int.Parse(parts[1]);
                                localColumns = new double[n][]; // Массив для быстрого доступа
                                b = new double[n];
                                columnCache.Clear();
                                elementCache.Clear();
                                
                                await writer.WriteLineAsync("READY");
                                Console.WriteLine($"[Worker {port}] Инициализирован с n={n}");
                            }
                            else if (line.StartsWith("COL"))
                            {
                                var parts = line.Split(' ');
                                if (parts.Length != 2)
                                {
                                    await writer.WriteLineAsync("ERROR: Неверный формат COL");
                                    continue;
                                }

                                int j = int.Parse(parts[1]);
                                var col = new double[n];
                                
                                // Читаем столбец пакетом - оптимизация для больших n
                                for (int i = 0; i < n; i++)
                                {
                                    string valueLine = await reader.ReadLineAsync();
                                    if (valueLine == null)
                                    {
                                        await writer.WriteLineAsync($"ERROR: Недостаточно данных для столбца {j}");
                                        break;
                                    }
                                    col[i] = double.Parse(valueLine);
                                }
                                
                                localColumns[j] = col;
                                // Добавляем в кэш
                                columnCache[j] = col;
                                
                                Console.WriteLine($"[Worker {port}] Получен столбец {j}");
                            }
                            else if (line == "VECTOR_B")
                            {
                                // Оптимизированное чтение вектора
                                for (int i = 0; i < n; i++)
                                {
                                    string valueLine = await reader.ReadLineAsync();
                                    if (valueLine == null)
                                    {
                                        await writer.WriteLineAsync($"ERROR: Недостаточно данных для вектора b");
                                        b = null;
                                        break;
                                    }
                                    b[i] = double.Parse(valueLine);
                                }
                                Console.WriteLine($"[Worker {port}] Получен вектор b (длина: {n})");
                            }
                            else if (line.StartsWith("GET_COLUMN"))
                            {
                                var parts = line.Split(' ');
                                if (parts.Length != 2)
                                {
                                    await writer.WriteLineAsync("ERROR: Неверный формат GET_COLUMN");
                                    continue;
                                }

                                int colIndex = int.Parse(parts[1]);
                                
                                // Проверяем кэш
                                if (!columnCache.TryGetValue(colIndex, out var column))
                                {
                                    column = localColumns[colIndex] ?? new double[n];
                                    columnCache[colIndex] = column;
                                }
                                
                                // Используем StringBuilder для быстрой отправки
                                var sb = new StringBuilder();
                                sb.AppendLine($"COLUMN {colIndex}");
                                for (int i = 0; i < n; i++)
                                {
                                    sb.AppendLine(column[i].ToString("R"));
                                }
                                
                                await writer.WriteAsync(sb.ToString());
                                // Console.WriteLine($"[Worker {port}] Отправлен столбец {colIndex}");
                            }
                            else if (line.StartsWith("SWAP_ROWS"))
                            {
                                var parts = line.Split(' ');
                                if (parts.Length != 3)
                                {
                                    await writer.WriteLineAsync("ERROR: Неверный формат SWAP_ROWS");
                                    continue;
                                }

                                int row1 = int.Parse(parts[1]);
                                int row2 = int.Parse(parts[2]);

                                // Быстрый обмен строк с проверкой границ
                                if (row1 >= 0 && row1 < n && row2 >= 0 && row2 < n)
                                {
                                    // Обмен в столбцах
                                    for (int colIdx = 0; colIdx < n; colIdx++)
                                    {
                                        var col = localColumns[colIdx];
                                        if (col != null)
                                        {
                                            (col[row1], col[row2]) = (col[row2], col[row1]);
                                        }
                                    }

                                    // Обмен в векторе b
                                    (b[row1], b[row2]) = (b[row2], b[row1]);
                                    
                                    // Очищаем кэш элементов, так как они изменились
                                    elementCache.Clear();
                                    // Очищаем кэш столбцов, которые были изменены
                                    columnCache.Clear();
                                }

                                await writer.WriteLineAsync("OK");
                                // Console.WriteLine($"[Worker {port}] Выполнен обмен строк {row1}<->{row2}");
                            }
                            else if (line.StartsWith("ELIMINATE_BATCH"))
                            {
                                var parts = line.Split(' ');
                                if (parts.Length != 3)
                                {
                                    await writer.WriteLineAsync("ERROR: Неверный формат ELIMINATE_BATCH");
                                    continue;
                                }

                                int k = int.Parse(parts[1]);
                                int totalOperations = int.Parse(parts[2]);

                                // Console.WriteLine($"[Worker {port}] Пакетная обработка: шаг {k}, операций: {totalOperations}");

                                // Оптимизация: читаем все операции сразу в буфер
                                var operations = new (int i, double factor)[totalOperations];
                                for (int op = 0; op < totalOperations; op++)
                                {
                                    string dataLine = await reader.ReadLineAsync();
                                    if (dataLine == null)
                                    {
                                        await writer.WriteLineAsync($"ERROR: Неожиданный конец потока при чтении операции {op}");
                                        break;
                                    }

                                    if (dataLine == "END_BATCH")
                                    {
                                        // Console.WriteLine($"[Worker {port}] Предупреждение: получен END_BATCH раньше времени");
                                        break;
                                    }

                                    var dataParts = dataLine.Split(' ');
                                    if (dataParts.Length != 2)
                                    {
                                        // Console.WriteLine($"[Worker {port}] Предупреждение: неверный формат строки: '{dataLine}'");
                                        continue;
                                    }

                                    int i = int.Parse(dataParts[0]);
                                    double factor = double.Parse(dataParts[1]);
                                    operations[op] = (i, factor);
                                }

                                // Выполняем операции пакетом (быстрее чем по одной)
                                for (int op = 0; op < totalOperations; op++)
                                {
                                    var (i, factor) = operations[op];
                                    
                                    // Выполняем исключение в векторе b
                                    if (i < n && k < n)
                                    {
                                        b[i] -= factor * b[k];
                                    }

                                    // Обновляем все столбцы
                                    for (int colIdx = 0; colIdx < n; colIdx++)
                                    {
                                        var col = localColumns[colIdx];
                                        if (col != null && i < n && k < n)
                                        {
                                            col[i] -= factor * col[k];
                                        }
                                    }
                                }

                                // Читаем END_BATCH если он есть
                                string endCheck = await reader.ReadLineAsync();
                                if (endCheck != null && endCheck != "END_BATCH")
                                {
                                    // Console.WriteLine($"[Worker {port}] Предупреждение: ожидался END_BATCH, получено: '{endCheck}'");
                                }

                                // Очищаем кэши, так как данные изменились
                                elementCache.Clear();
                                columnCache.Clear();
                                
                                await writer.WriteLineAsync("OK");
                                // Console.WriteLine($"[Worker {port}] Пакет шага {k} обработан успешно");
                            }
                            else if (line.StartsWith("NORMALIZE_ROW"))
                            {
                                var parts = line.Split(' ');
                                if (parts.Length != 3)
                                {
                                    await writer.WriteLineAsync("ERROR: Неверный формат NORMALIZE_ROW");
                                    continue;
                                }
                                
                                int row = int.Parse(parts[1]);
                                double divisor = double.Parse(parts[2]);
                                
                                if (Math.Abs(divisor) < 1e-12)
                                {
                                    await writer.WriteLineAsync("ERROR: Деление на ноль");
                                    continue;
                                }
                                
                                // Быстрое деление строки
                                for (int colIdx = 0; colIdx < n; colIdx++)
                                {
                                    var col = localColumns[colIdx];
                                    if (col != null && row < n)
                                    {
                                        col[row] /= divisor;
                                    }
                                }
                                
                                // Обновляем элемент в векторе b
                                if (row < n)
                                {
                                    b[row] /= divisor;
                                }
                                
                                // Очищаем кэш элементов этой строки
                                var keysToRemove = elementCache.Keys.Where(key => key.Item1 == row).ToList();
                                foreach (var key in keysToRemove) elementCache.Remove(key);
                                
                                await writer.WriteLineAsync("OK");
                                // Console.WriteLine($"[Worker {port}] Нормализована строка {row}");
                            }
                            else if (line.StartsWith("GET_ELEMENT"))
                            {
                                var parts = line.Split(' ');
                                if (parts.Length != 3)
                                {
                                    await writer.WriteLineAsync("ERROR: Неверный формат GET_ELEMENT");
                                    continue;
                                }
                                
                                int row = int.Parse(parts[1]);
                                int colIndex = int.Parse(parts[2]);
                                
                                // Проверяем кэш элементов
                                var cacheKey = (row, colIndex);
                                if (!elementCache.TryGetValue(cacheKey, out double value))
                                {
                                    var column = localColumns[colIndex];
                                    if (column != null && row < column.Length)
                                    {
                                        value = column[row];
                                    }
                                    else
                                    {
                                        value = 0;
                                    }
                                    elementCache[cacheKey] = value;
                                }
                                
                                await writer.WriteLineAsync(value.ToString("R"));
                            }
                            else if (line == "GET_MULTIPLE_ELEMENTS")
                            {
                                // Новый протокол: получение нескольких элементов за один запрос
                                // Формат после команды: количество_элементов
                                // затем для каждого элемента: строка столбец
                                
                                string countLine = await reader.ReadLineAsync();
                                if (countLine == null || !int.TryParse(countLine, out int elementCount))
                                {
                                    await writer.WriteLineAsync("ERROR: Неверный формат GET_MULTIPLE_ELEMENTS");
                                    continue;
                                }
                                
                                // Используем StringBuilder для эффективной отправки
                                var resultBuilder = new StringBuilder();
                                
                                for (int elem = 0; elem < elementCount; elem++)
                                {
                                    string coordsLine = await reader.ReadLineAsync();
                                    if (coordsLine == null)
                                    {
                                        resultBuilder.AppendLine("0");
                                        continue;
                                    }
                                    
                                    var coords = coordsLine.Split(' ');
                                    if (coords.Length != 2)
                                    {
                                        resultBuilder.AppendLine("0");
                                        continue;
                                    }
                                    
                                    int row = int.Parse(coords[0]);
                                    int colIndex = int.Parse(coords[1]);
                                    
                                    // Проверяем кэш
                                    var cacheKey = (row, colIndex);
                                    if (!elementCache.TryGetValue(cacheKey, out double value))
                                    {
                                        var column = localColumns[colIndex];
                                        if (column != null && row < column.Length)
                                        {
                                            value = column[row];
                                        }
                                        else
                                        {
                                            value = 0;
                                        }
                                        elementCache[cacheKey] = value;
                                    }
                                    
                                    resultBuilder.AppendLine(value.ToString("R"));
                                }
                                
                                await writer.WriteAsync(resultBuilder.ToString());
                                // Console.WriteLine($"[Worker {port}] Отправлено {elementCount} элементов");
                            }
                            else if (line == "GET_MATRIX")
                            {
                                // Отправляем все столбцы, которые храним
                                var matrixBuilder = new StringBuilder();
                                int columnCount = 0;
                                
                                for (int colIdx = 0; colIdx < n; colIdx++)
                                {
                                    var column = localColumns[colIdx];
                                    if (column != null)
                                    {
                                        matrixBuilder.AppendLine($"COL {colIdx}");
                                        for (int row = 0; row < n; row++)
                                        {
                                            matrixBuilder.AppendLine(column[row].ToString("R"));
                                        }
                                        columnCount++;
                                    }
                                }
                                
                                matrixBuilder.AppendLine("END_MATRIX");
                                await writer.WriteAsync(matrixBuilder.ToString());
                                
                                Console.WriteLine($"[Worker {port}] Отправлена матрица ({columnCount} столбцов)");
                            }
                            else if (line == "DONE")
                            {
                                Console.WriteLine($"[Worker {port}] Работа завершена");
                                await writer.WriteLineAsync("BYE");
                                
                                // Освобождаем память
                                localColumns = null;
                                b = null;
                                columnCache.Clear();
                                elementCache.Clear();
                                
                                GC.Collect(); // Принудительный сбор мусора
                                break;
                            }
                            else if (line == "PING")
                            {
                                // Простая команда для проверки соединения
                                await writer.WriteLineAsync("PONG");
                            }
                            else if (line == "STATUS")
                            {
                                // Отправляем статистику по памяти
                                int storedColumns = 0;
                                if (localColumns != null)
                                {
                                    storedColumns = localColumns.Count(col => col != null);
                                }
                                
                                await writer.WriteLineAsync($"STATUS: n={n}, columns={storedColumns}, cache_size={elementCache.Count}");
                            }
                            else
                            {
                                Console.WriteLine($"[Worker {port}] Неизвестная команда: '{line}'");
                                await writer.WriteLineAsync($"ERROR: Неизвестная команда: {line}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Worker {port}] Ошибка при обработке команды '{line}': {ex.Message}");
                            await writer.WriteLineAsync($"ERROR: {ex.Message}");
                        }
                    }
                }
            }
        }
        catch (IOException ioEx) when (ioEx.InnerException is SocketException sockEx && sockEx.SocketErrorCode == SocketError.TimedOut)
        {
            Console.WriteLine($"[Worker {port}] Таймаут соединения");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Worker {port}] Критическая ошибка: {ex.Message}");
            Console.WriteLine($"[Worker {port}] StackTrace: {ex.StackTrace}");
        }
        finally
        {
            Console.WriteLine($"[Worker {port}] Соединение закрыто");
        }
    }
}