// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using SemanticKernel.Data.Nl2Sql.Library.Schema;
using SemanticSchemaColumn = SemanticKernel.Data.Nl2Sql.Library.Schema.SchemaColumn;
namespace SemanticKernel.Data.Nl2Sql.Harness;

internal sealed class SqlSchemaProvider
{
    private readonly MySqlConnection _connection;

    public SqlSchemaProvider(MySqlConnection connection)
    {
        this._connection = connection;
    }

    public async Task<SchemaDefinition> GetSchemaAsync(string? description, params string[] tableNames)
    {
        var tableFilter = new HashSet<string>(tableNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        var tables =
            await this.QueryTablesAsync()
                .Where(t => tableFilter.Count == 0 || tableFilter.Contains(t.Name))
                .ToArrayAsync()
                .ConfigureAwait(false);

        return new SchemaDefinition(this._connection.Database, "Microsoft SQL Server", description, tables);
    }

    private async IAsyncEnumerable<SchemaTable> QueryTablesAsync()
    {
        var columnMap = new Dictionary<string, LinkedList<SemanticSchemaColumn>>(StringComparer.InvariantCultureIgnoreCase);
        var viewMap = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
        var keyMap = await this.QueryReferencesAsync().ConfigureAwait(false);
        var tableDescription = await this.QueryTableDescriptionsAsync().ConfigureAwait(false);

        using var reader = await this.ExecuteQueryAsync(Statements.DescribeColumns).ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var schemaName = reader.GetString(Statements.Columns.SchemaName);
            var tableName = reader.GetString(Statements.Columns.TableName);
            var fullName = FormatName(schemaName, tableName);

            if (!columnMap.TryGetValue(fullName, out var columns))
            {
                columns = new LinkedList<SemanticSchemaColumn>();
                columnMap[fullName] = columns;
            }

            var columnName = reader.GetString(Statements.Columns.ColumnName);
            var columnDesc = reader.IsDBNull(Statements.Columns.ColumnDesc) ? null : reader.GetString(Statements.Columns.ColumnDesc);
            var columnType = reader.GetString(Statements.Columns.ColumnType);
            var isPk = reader.GetBoolean(Statements.Columns.IsPk);

            if (reader.GetBoolean(Statements.Columns.IsView))
            {
                viewMap.Add(fullName);
            }

            keyMap.TryGetValue(FormatName(schemaName, tableName, columnName), out var reference);

            columns.AddLast(new SemanticSchemaColumn(columnName, columnDesc, columnType, isPk, reference.table, reference.column));
        }

        foreach (var kvp in columnMap)
        {
            tableDescription.TryGetValue(kvp.Key, out var description);
            yield return new SchemaTable(kvp.Key, description, viewMap.Contains(kvp.Key), kvp.Value.ToArray());
        }
    }

    private async Task<Dictionary<string, (string table, string column)>> QueryReferencesAsync()
    {
        var keyMap = new Dictionary<string, (string table, string column)>(StringComparer.OrdinalIgnoreCase);

        using var reader = await this.ExecuteQueryAsync(Statements.DescribeReferences).ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var schemaName = reader.GetString(Statements.Columns.SchemaName);
            var tableName = reader.GetString(Statements.Columns.TableName);
            var columnName = reader.GetString(Statements.Columns.ColumnName);
            var tableRefName = reader.GetString(Statements.Columns.ReferencedTableName);
            var columnRefName = reader.GetString(Statements.Columns.ReferencedColumnName);

            keyMap.Add(FormatName(schemaName, tableName, columnName), (FormatName(schemaName, tableRefName), columnRefName));
        }

        return keyMap;
    }

    private async Task<Dictionary<string, string?>> QueryTableDescriptionsAsync()
    {
        var tableMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        using var reader = await this.ExecuteQueryAsync(Statements.DescribeTables).ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var schemaName = reader.GetString(Statements.Columns.SchemaName);
            var tableName = reader.GetString(Statements.Columns.TableName);
            var tableDesc = reader.IsDBNull(Statements.Columns.TableDesc) ? null : reader.GetString(Statements.Columns.TableDesc);

            tableMap.Add(FormatName(schemaName, tableName), tableDesc);
        }

        return tableMap;
    }

    private async Task<DbDataReader> ExecuteQueryAsync(string statement)
    {
        using var cmd = this._connection.CreateCommand();

        // Security warning: ensure that the statement is not vulnerable to SQL injection
        cmd.CommandText = statement;

        return await cmd.ExecuteReaderAsync().ConfigureAwait(false);
    }

    public static string FormatName(params string[] parts)
    {
        return string.Join(CultureInfo.InvariantCulture.NumberFormat.NumberDecimalSeparator, parts);
    }

    private static class Statements
    {
        public static class Columns
        {
            public const string SchemaName = nameof(SchemaName);
            public const string TableName = nameof(TableName);
            public const string TableDesc = nameof(TableDesc);
            public const string ColumnName = nameof(ColumnName);
            public const string ColumnDesc = nameof(ColumnDesc);
            public const string ColumnType = nameof(ColumnType);
            public const string IsPk = nameof(IsPk);
            public const string IsView = nameof(IsView);
            public const string ReferencedTableName = nameof(ReferencedTableName);
            public const string ReferencedColumnName = nameof(ReferencedColumnName);
        }

        public const string DescribeTables =
            @"SELECT 
    TABLE_SCHEMA AS 'SchemaName',
    TABLE_NAME AS 'TableName'
FROM 
    INFORMATION_SCHEMA.TABLES
WHERE 
    TABLE_TYPE = 'BASE TABLE'
    AND TABLE_SCHEMA = 'test'
";

        public const string DescribeColumns =
            @"SELECT 
    TABLE_SCHEMA AS 'SchemaName',
    TABLE_NAME AS 'TableName',
    COLUMN_NAME AS 'ColumnName',
    DATA_TYPE AS 'ColumnType',
    COLUMN_KEY = 'PRI' AS IsPK
FROM 
    INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA  = 'test'
ORDER BY 
    TABLE_SCHEMA, 
    TABLE_NAME, 
    ORDINAL_POSITION
";

        public const string DescribeReferences =
            @"SELECT 
    TABLE_SCHEMA AS 'SchemaName',
    TABLE_NAME AS 'TableName',
    COLUMN_NAME AS 'ColumnName',
    REFERENCED_TABLE_NAME AS 'ReferencedTableName',
    REFERENCED_COLUMN_NAME AS 'ReferencedColumnName'
FROM 
    INFORMATION_SCHEMA.KEY_COLUMN_USAGE
WHERE 
    REFERENCED_TABLE_SCHEMA IS NOT NULL
";
    }
}
