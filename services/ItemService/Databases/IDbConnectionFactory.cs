using MySqlConnector;

namespace ItemService.Databases;

public interface IDbConnectionFactory
{
    MySqlConnection Create();
}
