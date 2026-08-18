using MySqlConnector;

namespace AuthService.Databases;

public class DbConnectionFactory : IDbConnectionFactory
{
    private readonly IConfiguration _configuration;

    public DbConnectionFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public MySqlConnection Create()
    {
        var connectionString = _configuration.GetConnectionString("MySql");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("MySQL connection string 'ConnectionStrings:MySql' is not configured.");
        }

        return new MySqlConnection(connectionString);
    }
}
