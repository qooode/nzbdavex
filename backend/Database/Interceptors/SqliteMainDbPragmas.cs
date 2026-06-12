using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NzbWebDAV.Database.Interceptors;

/// <summary>
/// Applies connection PRAGMAs for the main application database (db.sqlite).
///
/// The important one is WAL: in SQLite's default DELETE journal mode a writer takes an
/// exclusive lock over the whole file and blocks every reader and writer, so the
/// write-heavy Watchtower loop serializes against WebDAV browsing, the download queue
/// and config reads — the whole app appears to lock up while Watchtower iterates. WAL
/// lets readers run concurrently with a single writer, and busy_timeout makes the rare
/// writer-vs-writer collision wait briefly instead of failing with "database is locked".
///
/// synchronous=NORMAL is the standard WAL companion (a power-loss can lose only the last
/// committed transaction, never corrupt the file) and removes an fsync from every commit.
/// foreign_keys is per-connection and preserves the previous SqliteForeignKeyEnabler behavior.
/// </summary>
public class SqliteMainDbPragmas : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "PRAGMA foreign_keys = ON;" +
            "PRAGMA journal_mode = WAL;" +
            "PRAGMA busy_timeout = 5000;" +
            "PRAGMA synchronous = NORMAL;" +
            "PRAGMA temp_store = MEMORY;";
        command.ExecuteNonQuery();
    }
}
