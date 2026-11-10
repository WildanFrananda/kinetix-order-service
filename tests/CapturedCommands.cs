using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Kinetix.OrderService.Tests;

public sealed class CapturedCommands : DbCommandInterceptor {
    private readonly List<NpgsqlCommand> _commands = [];

    public IReadOnlyList<NpgsqlCommand> Commands => _commands;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result
    ) {
        _commands.Add(((NpgsqlCommand)command).Clone());
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default
    ) {
        _commands.Add(((NpgsqlCommand)command).Clone());
        return ValueTask.FromResult(result);
    }
}
