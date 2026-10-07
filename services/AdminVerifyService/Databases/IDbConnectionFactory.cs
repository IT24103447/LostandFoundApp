using MySqlConnector;

namespace AdminVerifyService.Databases;

public interface IDbConnectionFactory
{
    MySqlConnection Create();
}
