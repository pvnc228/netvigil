using Microsoft.EntityFrameworkCore;

namespace NetVigil.Server.Data
{
    public static class SchemaMigrator
    {
        public static async Task ApplyAsync(NetVigilDbContext db, ILogger logger)
        {
            var isSqlite = db.Database.IsSqlite();

            await TryRunAsync(db, logger,
                "ALTER TABLE Devices ADD COLUMN IsFlagged INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE \"Devices\" ADD COLUMN IF NOT EXISTS \"IsFlagged\" BOOLEAN NOT NULL DEFAULT FALSE",
                isSqlite);

            await TryRunAsync(db, logger,
                "ALTER TABLE Devices ADD COLUMN FlaggedAt TEXT NULL",
                "ALTER TABLE \"Devices\" ADD COLUMN IF NOT EXISTS \"FlaggedAt\" TIMESTAMP WITH TIME ZONE NULL",
                isSqlite);

            await TryRunAsync(db, logger,
                "ALTER TABLE Devices ADD COLUMN FlaggedBy TEXT NULL",
                "ALTER TABLE \"Devices\" ADD COLUMN IF NOT EXISTS \"FlaggedBy\" VARCHAR(64) NULL",
                isSqlite);

            await TryRunAsync(db, logger,
                @"CREATE TABLE IF NOT EXISTS DeviceFlagAudits (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    DeviceMac TEXT NOT NULL,
                    IsFlagged INTEGER NOT NULL,
                    ChangedAt TEXT NOT NULL,
                    ChangedBy TEXT NOT NULL,
                    Reason TEXT NULL
                )",
                @"CREATE TABLE IF NOT EXISTS ""DeviceFlagAudits"" (
                    ""Id"" BIGSERIAL PRIMARY KEY,
                    ""DeviceMac"" VARCHAR(32) NOT NULL,
                    ""IsFlagged"" BOOLEAN NOT NULL,
                    ""ChangedAt"" TIMESTAMP WITH TIME ZONE NOT NULL,
                    ""ChangedBy"" VARCHAR(64) NOT NULL,
                    ""Reason"" VARCHAR(256) NULL
                )",
                isSqlite);

            await TryRunAsync(db, logger,
                "CREATE INDEX IF NOT EXISTS IX_DeviceFlagAudits_DeviceMac_ChangedAt ON DeviceFlagAudits (DeviceMac, ChangedAt)",
                "CREATE INDEX IF NOT EXISTS \"IX_DeviceFlagAudits_DeviceMac_ChangedAt\" ON \"DeviceFlagAudits\" (\"DeviceMac\", \"ChangedAt\")",
                isSqlite);

            await TryRunAsync(db, logger,
                "CREATE INDEX IF NOT EXISTS IX_DeviceFlagAudits_ChangedAt ON DeviceFlagAudits (ChangedAt)",
                "CREATE INDEX IF NOT EXISTS \"IX_DeviceFlagAudits_ChangedAt\" ON \"DeviceFlagAudits\" (\"ChangedAt\")",
                isSqlite);

            await TryRunAsync(db, logger,
                "ALTER TABLE Users ADD COLUMN MustChangePassword INTEGER NOT NULL DEFAULT 0",
                "ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"MustChangePassword\" BOOLEAN NOT NULL DEFAULT FALSE",
                isSqlite);
        }

        public static async Task ApplyTimescaleAsync(NetVigilDbContext db, ILogger logger)
        {
            if (db.Database.IsSqlite()) return;

            try
            {
                await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE");
            }
            catch (Exception ex)
            {
                logger.LogWarning("TimescaleDB extension unavailable, leaving tables as plain Postgres ({Error})", ex.GetBaseException().Message);
                return;
            }

            await PromoteToHypertableAsync(db, logger, "TrafficSamples", "PK_TrafficSamples");
            await PromoteToHypertableAsync(db, logger, "Anomalies", "PK_Anomalies");
        }

        private static async Task PromoteToHypertableAsync(NetVigilDbContext db, ILogger logger, string table, string pkName)
        {
            try
            {
                var alreadyHyper = false;
                var conn = db.Database.GetDbConnection();
                await conn.OpenAsync();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT 1 FROM timescaledb_information.hypertables WHERE hypertable_name = @t";
                    var p = cmd.CreateParameter(); p.ParameterName = "@t"; p.Value = table; cmd.Parameters.Add(p);
                    var result = await cmd.ExecuteScalarAsync();
                    alreadyHyper = result is not null;
                }
                finally { await conn.CloseAsync(); }

                if (alreadyHyper)
                {
                    logger.LogDebug("{Table} is already a hypertable — skipping promote", table);
                    return;
                }

                await TryRunPostgresAsync(db, logger,
                    $"ALTER TABLE \"{table}\" DROP CONSTRAINT IF EXISTS \"{pkName}\"");
                await TryRunPostgresAsync(db, logger,
                    $"ALTER TABLE \"{table}\" ADD CONSTRAINT \"{pkName}\" PRIMARY KEY (\"Id\", \"Timestamp\")");
                await TryRunPostgresAsync(db, logger,
                    $"SELECT create_hypertable('\"{table}\"', 'Timestamp', if_not_exists => TRUE, migrate_data => TRUE)");

                logger.LogInformation("Converted {Table} to a TimescaleDB hypertable", table);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to promote {Table} to a hypertable — continuing with plain Postgres table", table);
            }
        }

        private static async Task TryRunPostgresAsync(NetVigilDbContext db, ILogger logger, string sql)
        {
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Postgres step skipped ({Message}): {Sql}",
                    ex.GetBaseException().Message,
                    sql.Length > 100 ? sql[..100] + "…" : sql);
            }
        }

        private static async Task TryRunAsync(
            NetVigilDbContext db, ILogger logger, string sqliteSql, string postgresSql, bool isSqlite)
        {
            var sql = isSqlite ? sqliteSql : postgresSql;
            try
            {
                await db.Database.ExecuteSqlRawAsync(sql);
            }
            catch (Exception ex)
            {
                logger.LogDebug("Schema migration step skipped ({Message}): {Sql}",
                    ex.Message, sql.Length > 80 ? sql[..80] + "…" : sql);
            }
        }
    }
}
