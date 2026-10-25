using Npgsql;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public static class DatabaseOutage {
    private static readonly HashSet<string> ServerUnavailableStates = new(StringComparer.Ordinal) {
        PostgresErrorCodes.AdminShutdown,
        PostgresErrorCodes.CrashShutdown,
        PostgresErrorCodes.CannotConnectNow,
        PostgresErrorCodes.TooManyConnections,
    };

    private const string ConnectionExceptionClass = "08";

    public static bool Explains(Exception exception) {
        for (Exception? cause = exception; cause is not null; cause = cause.InnerException) {
            if (cause is PostgresException postgres) {
                return postgres.SqlState.StartsWith(ConnectionExceptionClass, StringComparison.Ordinal)
                    || ServerUnavailableStates.Contains(postgres.SqlState);
            }

            if (cause is NpgsqlException npgsql) {
                return npgsql.IsTransient;
            }
        }

        return false;
    }
}
