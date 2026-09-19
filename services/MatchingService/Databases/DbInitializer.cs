using System.Reflection;
using MySqlConnector;

namespace MatchingService.Databases;

public static class DbInitializer
{
    public static void RunPendingMigrations(
        IServiceProvider services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MySql");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:MySql is required.");
        }

        var settings = new MySqlConnectionStringBuilder(connectionString);

        if (!string.Equals(
                settings.Database,
                "matching_service",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The configured database must be matching_service.");
        }

        var serverSettings = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = string.Empty
        };

        using (var server = new MySqlConnection(
                   serverSettings.ConnectionString))
        {
            server.Open();

            using var createDatabase = new MySqlCommand(
                """
                CREATE DATABASE IF NOT EXISTS matching_service
                    CHARACTER SET utf8mb4
                    COLLATE utf8mb4_unicode_ci;
                """,
                server);

            createDatabase.ExecuteNonQuery();
        }

        using var scope = services.CreateScope();

        var factory = scope.ServiceProvider
            .GetRequiredService<IDbConnectionFactory>();

        using var connection = factory.Create();
        connection.Open();

        using var acquireLock = new MySqlCommand(
            "SELECT GET_LOCK('matching_service_migrations', 30);",
            connection);

        if (Convert.ToInt32(acquireLock.ExecuteScalar()) != 1)
        {
            throw new InvalidOperationException(
                "Could not acquire the Matching Service migration lock.");
        }

        try
        {
            ApplyMigrations(connection);
        }
        finally
        {
            using var releaseLock = new MySqlCommand(
                "SELECT RELEASE_LOCK('matching_service_migrations');",
                connection);

            releaseLock.ExecuteScalar();
        }
    }

    private static void ApplyMigrations(MySqlConnection connection)
    {
        using (var createTable = new MySqlCommand(
                   """
                   CREATE TABLE IF NOT EXISTS _migrations (
                       filename VARCHAR(255) NOT NULL PRIMARY KEY,
                       applied_at DATETIME(3) NOT NULL
                           DEFAULT CURRENT_TIMESTAMP(3)
                   ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
                   """,
                   connection))
        {
            createTable.ExecuteNonQuery();
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);

        using (var command = new MySqlCommand(
                   "SELECT filename FROM _migrations;",
                   connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                applied.Add(reader.GetString(0));
            }
        }

        var assembly = Assembly.GetExecutingAssembly();

        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(
                "MatchingService.Databases.Migrations.",
                StringComparison.Ordinal))
            .Where(name => name.EndsWith(
                ".sql",
                StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

        foreach (var resourceName in resources)
        {
            // Preserve the migration identifiers used by Stage 1.
            var filename = Path.GetFileName(resourceName);

            if (applied.Contains(filename))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    "An embedded migration could not be loaded.");

            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();

            // MySQL DDL commits implicitly. Record success after execution.
            using (var migration = new MySqlCommand(sql, connection))
            {
                migration.ExecuteNonQuery();
            }

            using var record = new MySqlCommand(
                "INSERT INTO _migrations (filename) VALUES (@filename);",
                connection);

            record.Parameters.AddWithValue("@filename", filename);
            record.ExecuteNonQuery();
        }
    }
}