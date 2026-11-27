using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Passenger.Linq;

/// <summary>Supported data source kinds for strongly-typed LINQ queries.</summary>
public enum LinqDataSourceKind
{
    Sqlite,
    PostgreSql,
    SqlServer,
    MySql,
    Csv,
    Xlsx,
    Json,
    WorkspaceDbContext,
}

/// <summary>A column discovered from a database table, CSV/XLSX sheet, or JSON property.</summary>
public sealed record LinqColumnSchema(
    string Name,
    string PropertyName,
    string ClrTypeName,
    bool IsNullable,
    bool IsPrimaryKey);

/// <summary>A table, view, worksheet, JSON collection, or EF DbSet discovered from a data source.</summary>
public sealed record LinqTableSchema(
    string SchemaName,
    string TableName,
    string PropertyName,
    string EntityTypeName,
    IReadOnlyList<LinqColumnSchema> Columns);

/// <summary>Full schema snapshot for a data connection, used to populate Connections tree and generate TypedDataContext.g.cs.</summary>
public sealed record LinqDatabaseSchema(
    string ConnectionId,
    string ConnectionName,
    LinqDataSourceKind Kind,
    IReadOnlyList<LinqTableSchema> Tables);

/// <summary>Saved data connection definition (connection string, file path, or workspace DbContext; secret kept in env/store).</summary>
public sealed record LinqConnectionInfo(
    string Id,
    string Name,
    LinqDataSourceKind Kind,
    string Target,
    string? IncludeSchemasRegex = null,
    string? ExcludeSchemasRegex = null,
    string? IncludeTablesRegex = null,
    string? ExcludeTablesRegex = null,
    string? DbContextProjectPath = null,
    string? DbContextTypeName = null);

/// <summary>Probes schema for SQLite, PostgreSQL, SQL Server, MySQL, CSV, XLSX (ZipArchive + OpenXML), JSON, and Workspace EF Core DbContext.</summary>
public static class LinqSchemaProber
{
    public static async Task<LinqDatabaseSchema> ProbeAsync(LinqConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        LinqDatabaseSchema raw = connection.Kind switch
        {
            LinqDataSourceKind.Sqlite => await ProbeSqliteAsync(connection, cancellationToken).ConfigureAwait(false),
            LinqDataSourceKind.PostgreSql => await ProbePostgresAsync(connection, cancellationToken).ConfigureAwait(false),
            LinqDataSourceKind.SqlServer => await ProbeSqlServerAsync(connection, cancellationToken).ConfigureAwait(false),
            LinqDataSourceKind.MySql => await ProbeMySqlAsync(connection, cancellationToken).ConfigureAwait(false),
            LinqDataSourceKind.Csv => ProbeCsv(connection),
            LinqDataSourceKind.Xlsx => ProbeXlsx(connection),
            LinqDataSourceKind.Json => ProbeJson(connection),
            LinqDataSourceKind.WorkspaceDbContext => throw new InvalidOperationException("WorkspaceDbContext is resolved directly from project assemblies."),
            _ => throw new ArgumentOutOfRangeException(nameof(connection)),
        };
        return ApplyFilters(raw, connection);
    }

    public static async Task<string?> TestConnectionAsync(LinqConnectionInfo connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            switch (connection.Kind)
            {
                case LinqDataSourceKind.Sqlite:
                    string sqliteStr = connection.Target.Contains('=', StringComparison.Ordinal)
                        ? connection.Target
                        : new SqliteConnectionStringBuilder { DataSource = connection.Target, Mode = SqliteOpenMode.ReadOnly }.ToString();
                    await using (SqliteConnection c = new(sqliteStr))
                    {
                        await c.OpenAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return null;
                case LinqDataSourceKind.PostgreSql:
                    await using (NpgsqlConnection c = new(connection.Target))
                    {
                        await c.OpenAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return null;
                case LinqDataSourceKind.SqlServer:
                    await using (SqlConnection c = new(connection.Target))
                    {
                        await c.OpenAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return null;
                case LinqDataSourceKind.MySql:
                    await using (MySqlConnection c = new(connection.Target))
                    {
                        await c.OpenAsync(cancellationToken).ConfigureAwait(false);
                    }
                    return null;
                case LinqDataSourceKind.Csv:
                case LinqDataSourceKind.Xlsx:
                case LinqDataSourceKind.Json:
                    return File.Exists(connection.Target) ? null : $"File not found: {connection.Target}";
                case LinqDataSourceKind.WorkspaceDbContext:
                    return !string.IsNullOrWhiteSpace(connection.DbContextProjectPath) && !string.IsNullOrWhiteSpace(connection.DbContextTypeName)
                        ? null
                        : "Select a project and DbContext type.";
                default:
                    return "Unsupported data source kind.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    public static LinqDatabaseSchema ApplyFilters(LinqDatabaseSchema schema, LinqConnectionInfo connection)
    {
        Regex? incSchema = CompileRegex(connection.IncludeSchemasRegex);
        Regex? excSchema = CompileRegex(connection.ExcludeSchemasRegex);
        Regex? incTable = CompileRegex(connection.IncludeTablesRegex);
        Regex? excTable = CompileRegex(connection.ExcludeTablesRegex);

        if (incSchema is null && excSchema is null && incTable is null && excTable is null)
        {
            return schema;
        }

        List<LinqTableSchema> filtered = [];
        foreach (LinqTableSchema t in schema.Tables)
        {
            string schemaName = t.SchemaName ?? string.Empty;
            if (incSchema is not null && !incSchema.IsMatch(schemaName)) { continue; }
            if (excSchema is not null && excSchema.IsMatch(schemaName)) { continue; }
            if (incTable is not null && !incTable.IsMatch(t.TableName)) { continue; }
            if (excTable is not null && excTable.IsMatch(t.TableName)) { continue; }
            filtered.Add(t);
        }
        return schema with { Tables = filtered };
    }

    private static Regex? CompileRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }
        return new Regex(pattern.Trim(), RegexOptions.IgnoreCase | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(250));
    }

    private static async Task<LinqDatabaseSchema> ProbeSqlServerAsync(LinqConnectionInfo connection, CancellationToken ct)
    {
        await using SqlConnection conn = new(connection.Target);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
            ;WITH pk AS (
                SELECT DISTINCT tc.TABLE_SCHEMA, tc.TABLE_NAME, kcu.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                  ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
            )
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE,
                   CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS IS_PK
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t
              ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            LEFT JOIN pk
              ON pk.TABLE_SCHEMA = c.TABLE_SCHEMA AND pk.TABLE_NAME = c.TABLE_NAME AND pk.COLUMN_NAME = c.COLUMN_NAME
            WHERE t.TABLE_TYPE IN ('BASE TABLE', 'VIEW')
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION;
            """;

        Dictionary<(string Schema, string Table), List<LinqColumnSchema>> byTable = new();
        Dictionary<(string Schema, string Table), HashSet<string>> colSets = new();
        await using SqlCommand cmd = new(sql, conn);
        await using SqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string table = r.GetString(1);
            string colName = r.GetString(2);
            string dataType = r.GetString(3);
            bool nullable = string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase);
            bool isPk = r.GetInt32(5) != 0;

            (string Schema, string Table) key = (schema, table);
            if (!byTable.TryGetValue(key, out List<LinqColumnSchema>? cols))
            {
                cols = [];
                byTable[key] = cols;
                colSets[key] = new HashSet<string>(StringComparer.Ordinal);
            }
            string prop = UniqueIdentifier(colName, colSets[key]);
            cols.Add(new LinqColumnSchema(colName, prop, MapSqlServerType(dataType), nullable && !isPk, isPk));
        }

        List<LinqTableSchema> tables = [];
        HashSet<string> usedProps = new(StringComparer.Ordinal);
        HashSet<string> usedTypes = new(StringComparer.Ordinal);
        foreach (((string schema, string table), List<LinqColumnSchema> cols) in byTable)
        {
            string rawProp = string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase) ? table : $"{schema}_{table}";
            string tableProp = UniqueIdentifier(rawProp, usedProps);
            string entityType = UniqueIdentifier(Singularize(tableProp) + "Row", usedTypes);
            tables.Add(new LinqTableSchema(schema, table, tableProp, entityType, cols));
        }
        return new LinqDatabaseSchema(connection.Id, connection.Name, connection.Kind, tables);
    }

    private static async Task<LinqDatabaseSchema> ProbeMySqlAsync(LinqConnectionInfo connection, CancellationToken ct)
    {
        await using MySqlConnection conn = new(connection.Target);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.COLUMN_KEY
            FROM INFORMATION_SCHEMA.COLUMNS c
            JOIN INFORMATION_SCHEMA.TABLES t
              ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
            WHERE c.TABLE_SCHEMA = DATABASE()
              AND t.TABLE_TYPE IN ('BASE TABLE', 'VIEW')
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION;
            """;

        Dictionary<(string Schema, string Table), List<LinqColumnSchema>> byTable = new();
        Dictionary<(string Schema, string Table), HashSet<string>> colSets = new();
        await using MySqlCommand cmd = new(sql, conn);
        await using MySqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string table = r.GetString(1);
            string colName = r.GetString(2);
            string dataType = r.GetString(3);
            bool nullable = string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase);
            bool isPk = string.Equals(r.GetString(5), "PRI", StringComparison.OrdinalIgnoreCase);

            (string Schema, string Table) key = (schema, table);
            if (!byTable.TryGetValue(key, out List<LinqColumnSchema>? cols))
            {
                cols = [];
                byTable[key] = cols;
                colSets[key] = new HashSet<string>(StringComparer.Ordinal);
            }
            string prop = UniqueIdentifier(colName, colSets[key]);
            cols.Add(new LinqColumnSchema(colName, prop, MapMySqlType(dataType), nullable && !isPk, isPk));
        }

        List<LinqTableSchema> tables = [];
        HashSet<string> usedProps = new(StringComparer.Ordinal);
        HashSet<string> usedTypes = new(StringComparer.Ordinal);
        foreach (((string schema, string table), List<LinqColumnSchema> cols) in byTable)
        {
            string tableProp = UniqueIdentifier(table, usedProps);
            string entityType = UniqueIdentifier(Singularize(tableProp) + "Row", usedTypes);
            tables.Add(new LinqTableSchema(string.Empty, table, tableProp, entityType, cols));
        }
        return new LinqDatabaseSchema(connection.Id, connection.Name, connection.Kind, tables);
    }

    public static LinqDatabaseSchema ProbeJson(LinqConnectionInfo connection)
    {
        string path = connection.Target;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("JSON file not found.", path);
        }
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using JsonDocument doc = JsonDocument.Parse(fs);

        List<LinqTableSchema> tables = [];
        HashSet<string> usedProps = new(StringComparer.Ordinal);
        HashSet<string> usedTypes = new(StringComparer.Ordinal);
        string baseName = Path.GetFileNameWithoutExtension(path);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            List<LinqColumnSchema> cols = InferJsonColumns(doc.RootElement);
            string prop = UniqueIdentifier(baseName, usedProps);
            string entity = UniqueIdentifier(Singularize(prop) + "Row", usedTypes);
            tables.Add(new LinqTableSchema(string.Empty, baseName, prop, entity, cols));
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Array)
                {
                    List<LinqColumnSchema> cols = InferJsonColumns(p.Value);
                    string prop = UniqueIdentifier(p.Name, usedProps);
                    string entity = UniqueIdentifier(Singularize(prop) + "Row", usedTypes);
                    tables.Add(new LinqTableSchema(string.Empty, p.Name, prop, entity, cols));
                }
            }
            if (tables.Count == 0)
            {
                List<LinqColumnSchema> cols = InferJsonObjectColumns([doc.RootElement]);
                string prop = UniqueIdentifier(baseName, usedProps);
                string entity = UniqueIdentifier(Singularize(prop) + "Row", usedTypes);
                tables.Add(new LinqTableSchema(string.Empty, baseName, prop, entity, cols));
            }
        }
        return new LinqDatabaseSchema(connection.Id, connection.Name, connection.Kind, tables);
    }

    private static List<LinqColumnSchema> InferJsonColumns(JsonElement array)
    {
        List<JsonElement> objs = [];
        int count = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                objs.Add(item);
            }
            if (++count >= 200)
            {
                break;
            }
        }
        if (objs.Count > 0)
        {
            return InferJsonObjectColumns(objs);
        }
        return [new LinqColumnSchema("Value", "Value", "string", true, false)];
    }

    private static List<LinqColumnSchema> InferJsonObjectColumns(IReadOnlyList<JsonElement> objects)
    {
        Dictionary<string, (bool SeenNull, bool HasNumber, bool HasDecimal, bool HasBool, bool HasString)> map = new(StringComparer.Ordinal);
        List<string> order = [];
        foreach (JsonElement obj in objects)
        {
            foreach (JsonProperty prop in obj.EnumerateObject())
            {
                if (!map.TryGetValue(prop.Name, out (bool SeenNull, bool HasNumber, bool HasDecimal, bool HasBool, bool HasString) state))
                {
                    order.Add(prop.Name);
                }
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        state.SeenNull = true;
                        break;
                    case JsonValueKind.Number:
                        state.HasNumber = true;
                        if (!prop.Value.TryGetInt64(out _))
                        {
                            state.HasDecimal = true;
                        }
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        state.HasBool = true;
                        break;
                    default:
                        state.HasString = true;
                        break;
                }
                map[prop.Name] = state;
            }
        }

        HashSet<string> used = new(StringComparer.Ordinal);
        List<LinqColumnSchema> cols = [];
        foreach (string name in order)
        {
            (bool SeenNull, bool HasNumber, bool HasDecimal, bool HasBool, bool HasString) s = map[name];
            string clr = s.HasString ? "string"
                : s.HasNumber && !s.HasBool ? (s.HasDecimal ? "decimal" : "long")
                : s.HasBool && !s.HasNumber ? "bool"
                : "string";
            bool nullable = s.SeenNull || clr == "string";
            cols.Add(new LinqColumnSchema(name, UniqueIdentifier(name, used), clr, nullable, false));
        }
        return cols;
    }

    private static async Task<LinqDatabaseSchema> ProbeSqliteAsync(LinqConnectionInfo connection, CancellationToken ct)
    {
        string connStr = connection.Target.Contains('=', StringComparison.Ordinal)
            ? connection.Target
            : new SqliteConnectionStringBuilder { DataSource = connection.Target, Mode = SqliteOpenMode.ReadOnly }.ToString();

        await using SqliteConnection conn = new(connStr);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        List<string> tableNames = [];
        await using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                tableNames.Add(reader.GetString(0));
            }
        }

        List<LinqTableSchema> tables = [];
        HashSet<string> usedProps = new(StringComparer.Ordinal);
        HashSet<string> usedTypes = new(StringComparer.Ordinal);
        foreach (string table in tableNames)
        {
            List<LinqColumnSchema> cols = [];
            HashSet<string> usedColProps = new(StringComparer.Ordinal);
            await using SqliteCommand colCmd = conn.CreateCommand();
            colCmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\");";
            await using SqliteDataReader r = await colCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                string colName = r.GetString(1);
                string declType = r.IsDBNull(2) ? string.Empty : r.GetString(2);
                bool notNull = r.GetInt32(3) != 0;
                bool pk = r.GetInt32(5) != 0;
                string clr = MapSqliteType(declType);
                string prop = UniqueIdentifier(colName, usedColProps);
                cols.Add(new LinqColumnSchema(colName, prop, clr, !notNull && !pk, pk));
            }
            string tableProp = UniqueIdentifier(table, usedProps);
            string entityType = UniqueIdentifier(Singularize(tableProp) + "Row", usedTypes);
            tables.Add(new LinqTableSchema(string.Empty, table, tableProp, entityType, cols));
        }

        return new LinqDatabaseSchema(connection.Id, connection.Name, connection.Kind, tables);
    }

    private static async Task<LinqDatabaseSchema> ProbePostgresAsync(LinqConnectionInfo connection, CancellationToken ct)
    {
        await using NpgsqlConnection conn = new(connection.Target);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
            SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.is_nullable,
                   EXISTS (
                       SELECT 1 FROM information_schema.table_constraints tc
                       JOIN information_schema.key_column_usage kcu
                         ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema
                       WHERE tc.constraint_type = 'PRIMARY KEY'
                         AND tc.table_schema = c.table_schema
                         AND tc.table_name = c.table_name
                         AND kcu.column_name = c.column_name
                   ) AS is_pk
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND t.table_type IN ('BASE TABLE', 'VIEW')
            ORDER BY c.table_schema, c.table_name, c.ordinal_position;
            """;

        Dictionary<(string Schema, string Table), List<LinqColumnSchema>> byTable = new();
        Dictionary<(string Schema, string Table), HashSet<string>> colSets = new();
        await using NpgsqlCommand cmd = new(sql, conn);
        await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            string schema = r.GetString(0);
            string table = r.GetString(1);
            string colName = r.GetString(2);
            string dataType = r.GetString(3);
            bool nullable = string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase);
            bool isPk = r.GetBoolean(5);

            (string Schema, string Table) key = (schema, table);
            if (!byTable.TryGetValue(key, out List<LinqColumnSchema>? cols))
            {
                cols = [];
                byTable[key] = cols;
                colSets[key] = new HashSet<string>(StringComparer.Ordinal);
            }
            string prop = UniqueIdentifier(colName, colSets[key]);
            cols.Add(new LinqColumnSchema(colName, prop, MapPostgresType(dataType), nullable && !isPk, isPk));
        }

        List<LinqTableSchema> tables = [];
        HashSet<string> usedProps = new(StringComparer.Ordinal);
        HashSet<string> usedTypes = new(StringComparer.Ordinal);
        foreach (((string schema, string table), List<LinqColumnSchema> cols) in byTable)
        {
            string rawProp = string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase) ? table : $"{schema}_{table}";
            string tableProp = UniqueIdentifier(rawProp, usedProps);
            string entityType = UniqueIdentifier(Singularize(tableProp) + "Row", usedTypes);
            tables.Add(new LinqTableSchema(schema, table, tableProp, entityType, cols));
        }
        return new LinqDatabaseSchema(connection.Id, connection.Name, connection.Kind, tables);
    }

    public static LinqDatabaseSchema ProbeCsv(LinqConnectionInfo connection)
    {
        string path = connection.Target;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("CSV file not found.", path);
        }
        List<string[]> rows = ReadCsvRows(path, maxRows: 200);
        if (rows.Count == 0)
        {
            return new LinqDatabaseSchema(connection.Id, connection.Name, connection.Kind, []);
        }
        string[] headers = rows[0];
        List<LinqColumnSchema> cols = InferTabularColumns(headers, rows.Skip(1));
        string baseName = Path.GetFileNameWithoutExtension(path);
        string prop = ToIdentifier(baseName, "Rows");
        return new LinqDatabaseSchema(connection.Id, connection.Name, connection.Kind,
            [new LinqTableSchema(string.Empty, baseName, prop, Singularize(prop) + "Row", cols)]);
    }

    public static LinqDatabaseSchema ProbeXlsx(LinqConnectionInfo connection)
    {
        string path = connection.Target;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("XLSX file not found.", path);
        }
        List<(string SheetName, List<string[]> Rows)> sheets = ReadXlsxSheets(path, maxRowsPerSheet: 200);
        List<LinqTableSchema> tables = [];
        HashSet<string> usedProps = new(StringComparer.Ordinal);
        HashSet<string> usedTypes = new(StringComparer.Ordinal);
        foreach ((string sheetName, List<string[]> rows) in sheets)
        {
            if (rows.Count == 0)
            {
                continue;
            }
            List<LinqColumnSchema> cols = InferTabularColumns(rows[0], rows.Skip(1));
            string prop = UniqueIdentifier(sheetName, usedProps);
            string entity = UniqueIdentifier(Singularize(prop) + "Row", usedTypes);
            tables.Add(new LinqTableSchema(string.Empty, sheetName, prop, entity, cols));
        }
        return new LinqDatabaseSchema(connection.Id, connection.Name, connection.Kind, tables);
    }

    public static List<string[]> ReadCsvRows(string path, int maxRows = int.MaxValue)
    {
        using StreamReader reader = new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        List<string[]> rows = [];
        while (rows.Count < maxRows && reader.ReadLine() is { } line)
        {
            rows.Add(ParseCsvLine(line));
        }
        return rows;
    }

    private static string[] ParseCsvLine(string line)
    {
        List<string> fields = [];
        StringBuilder cur = new();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cur.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    cur.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    fields.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(c);
                }
            }
        }
        fields.Add(cur.ToString());
        return [.. fields];
    }

    public static List<(string SheetName, List<string[]> Rows)> ReadXlsxSheets(string path, int maxRowsPerSheet = int.MaxValue)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using ZipArchive zip = new(fs, ZipArchiveMode.Read);

        List<string> sharedStrings = [];
        ZipArchiveEntry? sstEntry = zip.GetEntry("xl/sharedStrings.xml");
        if (sstEntry is not null)
        {
            using Stream s = sstEntry.Open();
            XDocument sstDoc = XDocument.Load(s);
            XNamespace ns = sstDoc.Root?.Name.Namespace ?? XNamespace.None;
            foreach (XElement si in sstDoc.Descendants(ns + "si"))
            {
                sharedStrings.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
            }
        }

        ZipArchiveEntry? wbEntry = zip.GetEntry("xl/workbook.xml");
        ZipArchiveEntry? relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (wbEntry is null || relsEntry is null)
        {
            return [];
        }

        Dictionary<string, string> relTargets = new(StringComparer.Ordinal);
        using (Stream rs = relsEntry.Open())
        {
            XDocument relDoc = XDocument.Load(rs);
            foreach (XElement rel in relDoc.Root?.Elements() ?? [])
            {
                string? id = (string?)rel.Attribute("Id");
                string? target = (string?)rel.Attribute("Target");
                if (id is not null && target is not null)
                {
                    string normalized = target.TrimStart('/');
                    if (!normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                    {
                        normalized = "xl/" + normalized;
                    }
                    relTargets[id] = normalized;
                }
            }
        }

        List<(string Name, string EntryPath)> sheetEntries = [];
        using (Stream ws = wbEntry.Open())
        {
            XDocument wbDoc = XDocument.Load(ws);
            XNamespace mainNs = wbDoc.Root?.Name.Namespace ?? XNamespace.None;
            XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            foreach (XElement sheet in wbDoc.Descendants(mainNs + "sheet"))
            {
                string name = (string?)sheet.Attribute("name") ?? "Sheet";
                string? rId = (string?)sheet.Attribute(relNs + "id");
                if (rId is not null && relTargets.TryGetValue(rId, out string? entryPath))
                {
                    sheetEntries.Add((name, entryPath));
                }
            }
        }

        List<(string SheetName, List<string[]> Rows)> result = [];
        foreach ((string name, string entryPath) in sheetEntries)
        {
            ZipArchiveEntry? sheetEntry = zip.GetEntry(entryPath);
            if (sheetEntry is null)
            {
                continue;
            }
            using Stream ss = sheetEntry.Open();
            XDocument sDoc = XDocument.Load(ss);
            XNamespace ns = sDoc.Root?.Name.Namespace ?? XNamespace.None;
            List<string[]> rows = [];
            foreach (XElement rowEl in sDoc.Descendants(ns + "row"))
            {
                if (rows.Count >= maxRowsPerSheet)
                {
                    break;
                }
                List<string> row = [];
                foreach (XElement cEl in rowEl.Elements(ns + "c"))
                {
                    int colIdx = ParseCellColumnIndex((string?)cEl.Attribute("r"), row.Count);
                    while (row.Count < colIdx)
                    {
                        row.Add(string.Empty);
                    }
                    string? cellType = (string?)cEl.Attribute("t");
                    string rawVal = (string?)cEl.Element(ns + "v") ?? string.Empty;
                    string val = cellType switch
                    {
                        "s" when int.TryParse(rawVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) && idx >= 0 && idx < sharedStrings.Count => sharedStrings[idx],
                        "inlineStr" => string.Concat(cEl.Descendants(ns + "t").Select(t => t.Value)),
                        "b" => rawVal == "1" ? "true" : "false",
                        _ => rawVal,
                    };
                    row.Add(val);
                }
                rows.Add([.. row]);
            }
            result.Add((name, rows));
        }
        return result;
    }

    private static int ParseCellColumnIndex(string? cellRef, int fallback)
    {
        if (string.IsNullOrEmpty(cellRef))
        {
            return fallback;
        }
        int col = 0;
        foreach (char ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z')
            {
                col = (col * 26) + (ch - 'A' + 1);
            }
            else if (ch is >= 'a' and <= 'z')
            {
                col = (col * 26) + (ch - 'a' + 1);
            }
            else
            {
                break;
            }
        }
        return col > 0 ? col - 1 : fallback;
    }

    private static List<LinqColumnSchema> InferTabularColumns(string[] headers, IEnumerable<string[]> dataRows)
    {
        List<string[]> sample = [.. dataRows];
        HashSet<string> used = new(StringComparer.Ordinal);
        List<LinqColumnSchema> cols = [];
        for (int i = 0; i < headers.Length; i++)
        {
            string rawName = string.IsNullOrWhiteSpace(headers[i]) ? $"Column{i + 1}" : headers[i].Trim();
            string prop = UniqueIdentifier(rawName, used);
            bool anyEmpty = false;
            bool allInt = true;
            bool allDecimal = true;
            bool allBool = true;
            bool allDate = true;
            bool hasNonEmpty = false;

            foreach (string[] row in sample)
            {
                string val = i < row.Length ? row[i].Trim() : string.Empty;
                if (val.Length == 0)
                {
                    anyEmpty = true;
                    continue;
                }
                hasNonEmpty = true;
                if (allInt && !long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) { allInt = false; }
                if (allDecimal && !decimal.TryParse(val, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) { allDecimal = false; }
                if (allBool && !bool.TryParse(val, out _)) { allBool = false; }
                if (allDate && !DateTime.TryParse(val, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) { allDate = false; }
            }

            string clr = !hasNonEmpty ? "string"
                : allBool ? "bool"
                : allInt ? "long"
                : allDecimal ? "decimal"
                : allDate ? "DateTime"
                : "string";
            cols.Add(new LinqColumnSchema(rawName, prop, clr, anyEmpty || clr == "string", false));
        }
        return cols;
    }

    private static string MapSqliteType(string declType)
    {
        string upper = declType.ToUpperInvariant();
        if (upper.Contains("INT", StringComparison.Ordinal)) { return "long"; }
        if (upper.Contains("REAL", StringComparison.Ordinal) || upper.Contains("FLOA", StringComparison.Ordinal) || upper.Contains("DOUB", StringComparison.Ordinal)) { return "double"; }
        if (upper.Contains("NUM", StringComparison.Ordinal) || upper.Contains("DEC", StringComparison.Ordinal)) { return "decimal"; }
        if (upper.Contains("BOOL", StringComparison.Ordinal)) { return "bool"; }
        if (upper.Contains("BLOB", StringComparison.Ordinal)) { return "byte[]"; }
        if (upper.Contains("DATE", StringComparison.Ordinal) || upper.Contains("TIME", StringComparison.Ordinal)) { return "DateTime"; }
        return "string";
    }

    private static string MapPostgresType(string dataType) => dataType.ToLowerInvariant() switch
    {
        "smallint" or "int2" => "short",
        "integer" or "int" or "int4" => "int",
        "bigint" or "int8" => "long",
        "real" or "float4" => "float",
        "double precision" or "float8" => "double",
        "numeric" or "decimal" or "money" => "decimal",
        "boolean" or "bool" => "bool",
        "uuid" => "Guid",
        "bytea" => "byte[]",
        "date" or "timestamp without time zone" or "timestamp with time zone" or "timestamp" => "DateTime",
        _ => "string",
    };

    private static string MapSqlServerType(string dataType) => dataType.ToLowerInvariant() switch
    {
        "tinyint" => "byte",
        "smallint" => "short",
        "int" => "int",
        "bigint" => "long",
        "real" => "float",
        "float" => "double",
        "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
        "bit" => "bool",
        "uniqueidentifier" => "Guid",
        "varbinary" or "binary" or "image" => "byte[]",
        "date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime",
        "datetimeoffset" => "DateTimeOffset",
        "time" => "TimeSpan",
        _ => "string",
    };

    private static string MapMySqlType(string dataType) => dataType.ToLowerInvariant() switch
    {
        "tinyint" => "sbyte",
        "smallint" => "short",
        "mediumint" or "int" or "integer" => "int",
        "bigint" => "long",
        "float" => "float",
        "double" => "double",
        "decimal" or "numeric" => "decimal",
        "bit" => "bool",
        "blob" or "mediumblob" or "longblob" or "tinyblob" or "binary" or "varbinary" => "byte[]",
        "date" or "datetime" or "timestamp" => "DateTime",
        "time" => "TimeSpan",
        _ => "string",
    };

    private static string UniqueIdentifier(string raw, HashSet<string> used)
    {
        string baseId = ToIdentifier(raw, "Item");
        string candidate = baseId;
        int n = 2;
        while (!used.Add(candidate))
        {
            candidate = $"{baseId}{n++}";
        }
        return candidate;
    }

    internal static string ToIdentifier(string raw, string fallback)
    {
        StringBuilder sb = new();
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '_')
            {
                sb.Append('_');
            }
        }
        string s = sb.ToString().Trim('_');
        if (s.Length == 0)
        {
            return fallback;
        }
        if (char.IsDigit(s[0]))
        {
            s = "_" + s;
        }
        return IsCSharpKeyword(s) ? "@" + s : s;
    }

    private static string Singularize(string name)
    {
        if (name.Length > 3 && name.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^3] + "y";
        }
        if (name.Length > 2 && name.EndsWith('s') && !name.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
        {
            return name[..^1];
        }
        return name;
    }

    private static bool IsCSharpKeyword(string s) => s is
        "abstract" or "as" or "base" or "bool" or "break" or "byte" or "case" or "catch" or "char" or "checked" or
        "class" or "const" or "continue" or "decimal" or "default" or "delegate" or "do" or "double" or "else" or
        "enum" or "event" or "explicit" or "extern" or "false" or "finally" or "fixed" or "float" or "for" or
        "foreach" or "goto" or "if" or "implicit" or "in" or "int" or "interface" or "internal" or "is" or "lock" or
        "long" or "namespace" or "new" or "null" or "object" or "operator" or "out" or "override" or "params" or
        "private" or "protected" or "public" or "readonly" or "ref" or "return" or "sbyte" or "sealed" or "short" or
        "sizeof" or "stackalloc" or "static" or "string" or "struct" or "switch" or "this" or "throw" or "true" or
        "try" or "typeof" or "uint" or "ulong" or "unchecked" or "unsafe" or "ushort" or "using" or "virtual" or
        "void" or "volatile" or "while";
}
