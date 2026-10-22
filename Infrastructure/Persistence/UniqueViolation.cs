using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kinetix.OrderService.Infrastructure.Persistence;

public static class UniqueViolation {
    public static bool On(DbUpdateException exception, string indexName) =>
        exception.InnerException is PostgresException {
            SqlState: PostgresErrorCodes.UniqueViolation
        } violation
        && string.Equals(violation.ConstraintName, indexName, StringComparison.Ordinal);
}
