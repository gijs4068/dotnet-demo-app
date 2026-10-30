using Microsoft.Extensions.Configuration;
using MySqlConnector;
using TodoApp.Data;

namespace TodoApp.Tests;

public class TodoRepositoryFixture
{
    public TodoRepository Repository { get; }
    private readonly string _connectionString;

    public TodoRepositoryFixture()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Test.json", optional: false)
            .Build();

        _connectionString = configuration.GetConnectionString("TodoDb")
            ?? throw new InvalidOperationException("Test connection string not configured.");

        Repository = new TodoRepository(configuration);
    }

    public async Task ResetDatabaseAsync()
    {
        await using var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE todos;";
        await command.ExecuteNonQueryAsync();
    }
}