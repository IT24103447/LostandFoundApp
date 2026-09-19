using MySqlConnector;

namespace MatchingService.Databases;

public interface IDbConnectionFactory
{
    MySqlConnection Create();
}
