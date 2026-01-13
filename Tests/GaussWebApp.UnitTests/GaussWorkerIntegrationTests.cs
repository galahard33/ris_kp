using Xunit;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Text;
using System.IO;
using System;

namespace GaussWebApp.UnitTests
{
    [Trait("Category", "Integration")]
    [Trait("Requires", "Worker")]
    public class GaussWorkerTests : IDisposable
    {
        private readonly int[] _workerPorts = { 9001, 9002, 9003, 9004 };
        private bool _skipTests = false;
        
        public GaussWorkerTests()
        {
            Console.WriteLine("Проверка доступности воркеров...");

            _skipTests = false;
            
            foreach (var port in _workerPorts)
            {
                if (!IsPortOpen("localhost", port))
                {
                    Console.WriteLine($"Воркер на порту {port} не доступен");
                    _skipTests = true;
                    break;
                }
            }
            
            if (!_skipTests)
            {
                Console.WriteLine("Все воркеры доступны, запускаем тесты...");
            }
            else
            {
                Console.WriteLine("Тесты будут пропущены (воркеры не запущены)");
            }
        }

        public void Dispose()
        {

            GC.SuppressFinalize(this);
        }
        
        private bool IsPortOpen(string host, int port, int timeout = 2000)
        {
            try
            {
                using var client = new TcpClient();
                var result = client.BeginConnect(host, port, null, null);
                var success = result.AsyncWaitHandle.WaitOne(timeout);
                if (success)
                    client.EndConnect(result);
                return success;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> CheckWorkerAvailability(int port, int maxAttempts = 3)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    using var client = new TcpClient();
                    client.SendTimeout = 1000;
                    client.ReceiveTimeout = 1000;
                    
                    await client.ConnectAsync("localhost", port);
                    using var stream = client.GetStream();
                    using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    
                    await writer.WriteLineAsync("INIT 1");
                    var response = await reader.ReadLineAsync();
                    
                    if (response == "READY")
                    {
                        await writer.WriteLineAsync("DONE");
                        await reader.ReadLineAsync(); 
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Попытка {i + 1} для порта {port} не удалась: {ex.Message}");
                    if (i < maxAttempts - 1)
                        await Task.Delay(500);
                }
            }
            
            return false;
        }
        
        [Fact]
        public async Task Worker_AcceptsConnection_AndResponds()
        {
            if (_skipTests)
            {
                Console.WriteLine("Тест пропущен: воркеры не запущены");
                return;
            }

            var port = _workerPorts[0];
            
            using var client = new TcpClient();
            
            try
            {
                await client.ConnectAsync("localhost", port);
                using var stream = client.GetStream();
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
                using var reader = new StreamReader(stream, Encoding.UTF8);
                
                await writer.WriteLineAsync("INIT 3");
                var response = await reader.ReadLineAsync();

                Assert.Equal("READY", response);

                await writer.WriteLineAsync("DONE");
                var byeResponse = await reader.ReadLineAsync();
                Assert.Equal("BYE", byeResponse);
            }
            catch (Exception ex)
            {
                throw new Exception($"Ошибка при подключении к воркеру на порту {port}: {ex.Message}");
            }
        }
        
    }
}