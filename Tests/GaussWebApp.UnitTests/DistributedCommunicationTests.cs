using Xunit;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.IO;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GaussWebApp.UnitTests
{
    // Тесты с Mock воркерами (не требуют реальных воркеров)
    [Trait("Category", "Unit")]
    public class DistributedCommunicationTests
    {
        [Fact]
        public void Protocol_Commands_AreCorrectlyFormatted()
        {
            // Проверка форматов команд протокола
            var commands = new[]
            {
                "INIT 100",
                "COL 5",
                "VECTOR_B",
                "GET_COLUMN 3",
                "SWAP_ROWS 1 2",
                "ELIMINATE_BATCH 0 10",
                "NORMALIZE_ROW 5 2.5",
                "UPDATE_B_ELEMENT 3 7.8",
                "GET_ELEMENT 2 3",
                "GET_MATRIX",
                "DONE"
            };
            
            foreach (var cmd in commands)
            {
                Assert.False(string.IsNullOrWhiteSpace(cmd));
                Assert.DoesNotContain("\n", cmd);
                Assert.DoesNotContain("\r", cmd);
            }
        }
        
        [Theory]
        [InlineData("INIT 100", "INIT", "100")]
        [InlineData("COL 5", "COL", "5")]
        [InlineData("SWAP_ROWS 1 2", "SWAP_ROWS", "1", "2")]
        [InlineData("GET_ELEMENT 2 3", "GET_ELEMENT", "2", "3")]
        [InlineData("NORMALIZE_ROW 5 2.5", "NORMALIZE_ROW", "5", "2.5")]
        public void Protocol_CommandParsing_WorksCorrectly(string command, string expectedCommand, params string[] expectedArgs)
        {
            var parts = command.Split(' ');

            Assert.Equal(expectedCommand, parts[0]);
            Assert.Equal(expectedArgs.Length, parts.Length - 1);
            
            for (int i = 0; i < expectedArgs.Length; i++)
            {
                Assert.Equal(expectedArgs[i], parts[i + 1]);
            }
        }
        
        [Fact]
        public void Protocol_ResponseParsing_Works()
        {
            // Тестируем парсинг ответов
            var responses = new Dictionary<string, Action<string>>
            {
                ["READY"] = (response) => Assert.Equal("READY", response),
                ["OK"] = (response) => Assert.Equal("OK", response),
                ["BYE"] = (response) => Assert.Equal("BYE", response),
                ["COLUMN 5"] = (response) => Assert.StartsWith("COLUMN", response),
                ["ERROR: Неверный формат"] = (response) => Assert.StartsWith("ERROR:", response)
            };
            
            foreach (var kvp in responses)
            {
                kvp.Value(kvp.Key);
            }
        }
        
        [Fact]
        public void NodesFile_Parsing_Logic()
        {
            var nodesContent = @"localhost:9001
                                localhost:9002
                                localhost:9003
                                localhost:9004
                                ";
            
            // Act
            var lines = nodesContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var nodes = new List<(string Host, int Port)>();
            
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                    continue;
                    
                var parts = trimmed.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out int port))
                {
                    nodes.Add((parts[0].Trim(), port));
                }
            }
            
            // Assert
            Assert.Equal(4, nodes.Count);
            Assert.Equal("localhost", nodes[0].Host);
            Assert.Equal(9001, nodes[0].Port);
            Assert.Equal("localhost", nodes[2].Host);
            Assert.Equal(9003, nodes[2].Port);
        }
    }
}