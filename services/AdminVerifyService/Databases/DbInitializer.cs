using System.Reflection;
using MySqlConnector;

namespace AdminVerifyService.Databases;

public static class DbInitializer
{
    private const string DatabaseName = "admin_verify_db";
    private const string MigrationLock = "admin_verify_db_migrations";
    private const string MigrationPrefix = "AdminVerifyService.Databases.Migrations.";

    public static void RunPendingMigrations(
        IServiceProvider services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("MySql");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:MySql is required.");
        }

        if (!string.Equals(
                new MySqlConnectionStringBuilder(connectionString).Database,
                DatabaseName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The configured database must be {DatabaseName}.");
        }

        var serverSettings = new MySqlConnectionStringBuilder(connectionString)
        {
            Database = string.Empty
        };

        using (var server = new MySqlConnection(serverSettings.ConnectionString))
        {
            server.Open();

            using var createDatabase = new MySqlCommand(
                $"""
                CREATE DATABASE IF NOT EXISTS {DatabaseName}
                    CHARACTER SET utf8mb4
                    COLLATE utf8mb4_unicode_ci;
                """,
                server);

            createDatabase.ExecuteNonQuery();
        }

        using var connection = services
            .GetRequiredService<IDbConnectionFactory>()
            .Create();

        connection.Open();

        using var acquireLock = new MySqlCommand(
            $"SELECT GET_LOCK('{MigrationLock}', 30);",
            connection);

        if (Convert.ToInt32(acquireLock.ExecuteScalar()) != 1)
        {
            throw new InvalidOperationException(
                "Could not acquire the Admin Verify Service migration lock.");
        }

        try
        {
            ApplyMigrations(connection);
        }
        finally
        {
            using var releaseLock = new MySqlCommand(
                $"SELECT RELEASE_LOCK('{MigrationLock}');",
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

        using (var command = new MySqlCommand("SELECT filename FROM _migrations;", connection))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                applied.Add(reader.GetString(0));
            }
        }

        var assembly = Assembly.GetExecutingAssembly();

        var pending = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(MigrationPrefix, StringComparison.Ordinal) &&
                           name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => (Resource: name, Filename: name[MigrationPrefix.Length..]))
            .Where(migration => !applied.Contains(migration.Filename))
            .OrderBy(migration => migration.Filename, StringComparer.Ordinal);

        foreach (var (resource, filename) in pending)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("An embedded migration could not be loaded.");

            using var reader = new StreamReader(stream);

            using (var migration = new MySqlCommand(reader.ReadToEnd(), connection))
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
