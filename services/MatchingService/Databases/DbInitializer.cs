using System.Reflection;
using MySqlConnector;

namespace MatchingService.Databases;

public static class DbInitializer
{
    public static void RunPendingMigrations(IServiceProvider services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MySql");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "MySQL connection string 'ConnectionStrings:MySql' is not configured.");
        }

        var serverOnly = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = string.Empty
        };

        EnsureDatabaseExists(serverOnly.ConnectionString);

        var assembly = Assembly.GetExecutingAssembly();
        var migrationFiles = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(
                "MatchingService.Databases.Migrations.",
                StringComparison.Ordinal))
            .Where(name => name.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        EnsureMigrationsTableExists(factory);
        var applied = GetAppliedMigrations(factory);

        foreach (var resourceName in migrationFiles)
        {
            var filename = Path.GetFileName(resourceName);
            if (applied.Contains(filename))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Embedded migration '{resourceName}' was not found.");
            using var reader = new StreamReader(stream);
            ExecuteMigration(factory, reader.ReadToEnd(), filename);
        }
    }

    private static void EnsureDatabaseExists(string serverConnectionString)
    {
        using var connection = new MySqlConnection(serverConnectionString);
        connection.Open();
        using var command = new MySqlCommand(
            "CREATE DATABASE IF NOT EXISTS matching_service " +
            "CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;",
            connection);
        command.ExecuteNonQuery();
    }

    private static void EnsureMigrationsTableExists(IDbConnectionFactory factory)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS _migrations (
                filename VARCHAR(255) NOT NULL PRIMARY KEY,
                applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;

        using var connection = factory.Create();
        connection.Open();
        using var command = new MySqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static HashSet<string> GetAppliedMigrations(IDbConnectionFactory factory)
    {
        using var connection = factory.Create();
        connection.Open();
        using var command = new MySqlCommand("SELECT filename FROM _migrations;", connection);
        using var reader = command.ExecuteReader();
        var filenames = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            filenames.Add(reader.GetString(0));
        }

        return filenames;
    }

    private static void ExecuteMigration(
        IDbConnectionFactory factory,
        string sql,
        string filename)
    {
        using var connection = factory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        using (var migration = new MySqlCommand(sql, connection, transaction))
        {
            migration.ExecuteNonQuery();
        }

        using (var record = new MySqlCommand(
            "INSERT INTO _migrations (filename) VALUES (@filename);",
            connection,
            transaction))
        {
            record.Parameters.AddWithValue("@filename", filename);
            record.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
