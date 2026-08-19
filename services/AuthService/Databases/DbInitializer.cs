using System.Reflection;
using MySqlConnector;

namespace AuthService.Databases;

/// <summary>
/// Runs pending SQL migration scripts from the Migrations folder.
/// Tracks applied migrations in an `_migrations` table keyed by filename.
/// Intended to be invoked only in Development (see Program.cs) — never auto-runs in production.
/// </summary>
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

        // The first run will fail at Open() if the database doesn't exist yet (the connection
        // string references Database=auth_service). Build a "server-only" connection that omits
        // the Database key so we can run `CREATE DATABASE IF NOT EXISTS …` and `USE …` ourselves.
        var serverOnlyCs = new MySqlConnectionStringBuilder(connectionString);
        serverOnlyCs.Database = string.Empty;

        var assembly = Assembly.GetExecutingAssembly();
        var migrationFiles = assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith("AuthService.Databases.Migrations.", StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (migrationFiles.Count == 0)
        {
            return;
        }

        // Ensure the target database exists before anything that uses the regular connection.
        EnsureDatabaseExists(serverOnlyCs.ConnectionString);

        using var scope = services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();

        EnsureMigrationsTableExists(factory);
        var applied = GetAppliedMigrations(factory);

        foreach (var file in migrationFiles)
        {
            var shortName = Path.GetFileName(file);
            if (applied.Contains(shortName))
            {
                continue;
            }

            var sql = ReadEmbeddedScript(assembly, file);
            ExecuteMultiStatement(factory, sql, shortName);
            MarkApplied(factory, shortName);
        }
    }

    private static void EnsureDatabaseExists(string serverOnlyConnectionString)
    {
        // Open without a database and run the migration's CREATE DATABASE / USE / CREATE TABLE.
        using var conn = new MySqlConnection(serverOnlyConnectionString);
        conn.Open();

        // The V001 script begins with `CREATE DATABASE IF NOT EXISTS auth_service; USE auth_service; CREATE TABLE …`
        // MySqlConnector executes multi-statement commands when the SQL is assigned as a single command text.
        const string sql = """
            CREATE DATABASE IF NOT EXISTS auth_service
                CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            USE auth_service;
            """;
        using var pre = new MySqlCommand(sql, conn);
        pre.ExecuteNonQuery();
    }

    private static void EnsureMigrationsTableExists(IDbConnectionFactory factory)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS _migrations (
                id         INT AUTO_INCREMENT PRIMARY KEY,
                filename   VARCHAR(255) NOT NULL UNIQUE,
                applied_at DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
            """;
        using var conn = factory.Create();
        conn.Open();
        using var cmd = new MySqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static HashSet<string> GetAppliedMigrations(IDbConnectionFactory factory)
    {
        const string sql = "SELECT filename FROM _migrations;";
        using var conn = factory.Create();
        conn.Open();
        using var cmd = new MySqlCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }
        return set;
    }

    private static void ExecuteMultiStatement(IDbConnectionFactory factory, string sql, string name)
    {
        using var conn = factory.Create();
        conn.Open();
        using var cmd = new MySqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    private static void MarkApplied(IDbConnectionFactory factory, string filename)
    {
        const string sql = "INSERT INTO _migrations (filename) VALUES (@filename);";
        using var conn = factory.Create();
        conn.Open();
        using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@filename", filename);
        cmd.ExecuteNonQuery();
    }

    private static string ReadEmbeddedScript(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
