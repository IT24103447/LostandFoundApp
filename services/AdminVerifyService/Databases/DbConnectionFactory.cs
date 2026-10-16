using MySqlConnector;

namespace AdminVerifyService.Databases;

public sealed class DbConnectionFactory(IConfiguration configuration) : IDbConnectionFactory
{
    public MySqlConnection Create()
    {
        var connectionString = configuration.GetConnectionString("MySql");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "MySQL connection string 'ConnectionStrings:MySql' is not configured.");
        }

        return new MySqlConnection(connectionString);
    }
}
