using System.Reflection;
using MySqlConnector;

namespace ItemService.Databases;

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


        var serverOnlyCs = new MySqlConnectionStringBuilder(connectionString);
        serverOnlyCs.Database = string.Empty;

        var assembly = Assembly.GetExecutingAssembly();
        var migrationFiles = assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith("ItemService.Databases.Migrations.", StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (migrationFiles.Count == 0)
        {
            return;
        }

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
        
        using var conn = new MySqlConnection(serverOnlyConnectionString);
        conn.Open();

        
        const string sql = """
            CREATE DATABASE IF NOT EXISTS item_service
                CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
            USE item_service;
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
