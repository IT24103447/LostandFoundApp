using MySqlConnector;

namespace AuthService.Databases;

public interface IDbConnectionFactory
{
    MySqlConnection Create();
}
