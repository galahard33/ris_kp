using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

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

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"[Worker {port}] Слушаю порт {port}");

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
        Dictionary<int, double[]> localColumns = new();
        double[] b = null;
        int n = 0;

        try
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
            {
                Console.WriteLine($"[Worker {port}] Подключение установлено");

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
                            localColumns.Clear();
                            b = null;
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
                            Console.WriteLine($"[Worker {port}] Получен столбец {j}");
                        }
                        else if (line == "VECTOR_B")
                        {
                            b = new double[n];
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
                            if (localColumns.TryGetValue(colIndex, out var column))
                            {
                                await writer.WriteLineAsync($"COLUMN {colIndex}");
                                for (int i = 0; i < n; i++)
                                    await writer.WriteLineAsync(column[i].ToString("R"));
                            }
                            else
                            {
                                await writer.WriteLineAsync($"ERROR: Столбец {colIndex} не найден");
                            }
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

                            // Обмен в столбцах
                            foreach (var col in localColumns.Values)
                            {
                                if (row1 < col.Length && row2 < col.Length)
                                {
                                    (col[row1], col[row2]) = (col[row2], col[row1]);
                                }
                            }

                            // Обмен в векторе b
                            if (b != null && row1 < b.Length && row2 < b.Length)
                            {
                                (b[row1], b[row2]) = (b[row2], b[row1]);
                            }

                            await writer.WriteLineAsync("OK");
                            Console.WriteLine($"[Worker {port}] Выполнен обмен строк {row1}<->{row2}");
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

                            Console.WriteLine($"[Worker {port}] Пакетная обработка: шаг {k}, операций: {totalOperations}");

                            // Читаем все операции из пакета
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
                                    Console.WriteLine($"[Worker {port}] Предупреждение: получен END_BATCH раньше времени");
                                    break;
                                }

                                var dataParts = dataLine.Split(' ');
                                if (dataParts.Length != 2)
                                {
                                    Console.WriteLine($"[Worker {port}] Предупреждение: неверный формат строки: '{dataLine}'");
                                    continue;
                                }

                                int i = int.Parse(dataParts[0]);
                                double factor = double.Parse(dataParts[1]);

                                // Выполняем исключение
                                if (b != null && i < b.Length && k < b.Length)
                                {
                                    b[i] -= factor * b[k];
                                }

                                // Обновляем все столбцы
                                foreach (var col in localColumns.Values)
                                {
                                    if (i < col.Length && k < col.Length)
                                    {
                                        col[i] -= factor * col[k];
                                    }
                                }

                                // Логируем прогресс для больших пакетов
                                if (totalOperations > 100 && (op + 1) % 100 == 0)
                                {
                                    Console.WriteLine($"[Worker {port}] Обработано {op + 1}/{totalOperations}");
                                }
                            }

                            // Читаем END_BATCH, если он еще не получен
                            string endCheck = await reader.ReadLineAsync();
                            if (endCheck != null && endCheck != "END_BATCH")
                            {
                                Console.WriteLine($"[Worker {port}] Предупреждение: ожидался END_BATCH, получено: '{endCheck}'");
                            }

                            await writer.WriteLineAsync("OK");
                            Console.WriteLine($"[Worker {port}] Пакет шага {k} обработан успешно");
                        }
                        else if (line == "GET_MATRIX")
                        {
                            // Отправляем все столбцы, которые храним
                            foreach (var kvp in localColumns)
                            {
                                await writer.WriteLineAsync($"COL {kvp.Key}");
                                for (int row = 0; row < n; row++)
                                {
                                    if (row < kvp.Value.Length)
                                    {
                                        await writer.WriteLineAsync(kvp.Value[row].ToString("R"));
                                    }
                                    else
                                    {
                                        await writer.WriteLineAsync("0");
                                    }
                                }
                            }
                            await writer.WriteLineAsync("END_MATRIX");
                            Console.WriteLine($"[Worker {port}] Отправлена матрица ({localColumns.Count} столбцов)");
                        }
                        else if (line == "DONE")
                        {
                            Console.WriteLine($"[Worker {port}] Работа завершена");
                            await writer.WriteLineAsync("BYE");
                            break;
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