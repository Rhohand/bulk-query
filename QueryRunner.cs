﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace BulkQuery
{
    public static class QueryRunner
    {
        public static List<DatabaseDefinition> GetDatabasesForServer(ServerDefinition server)
        {
            var databases = new List<DatabaseDefinition>();
            const string query = "SELECT name FROM master.dbo.sysdatabases";
            var builder = new SqlConnectionStringBuilder(server.ConnectionString)
            {
                ConnectTimeout = 5
            };
            using (SqlConnection connection = new SqlConnection(builder.ConnectionString))
            {
                SqlCommand command = new SqlCommand(query, connection);
                connection.Open();
                SqlDataReader reader = command.ExecuteReader();
                try
                {
                    while (reader.Read())
                    {
                        var dbName = reader["name"] as string;
                        databases.Add(new DatabaseDefinition(dbName, server));
                    }
                }
                finally
                {
                    // Always call Close when done reading.
                    reader.Close();
                }
            }
            return databases;
        }

        private static bool AreColumnsIdentical(DataColumnCollection columnsA, DataColumnCollection columnsB)
        {
            if (columnsA.Count != columnsB.Count)
                return false;

            for (int i = 0; i < columnsA.Count; i++)
            {
                var colA = columnsA[i];
                var colB = columnsB[i];

                if (colA.DataType != colB.DataType || colA.ColumnName != colB.ColumnName || colA.Ordinal != colB.Ordinal)
                    return false;
            }

            return true;
        }

        public class QueryResult
        {
            public List<string> Messages { get; set; }
            public DataTable ResultTable { get; set; }
        }

        public static Task<QueryResult> BulkQuery(IList<DatabaseDefinition> databases, string query)
        {
            // Needed to push all async code to thread pool thread (otherwise UI thread attempts to keep thread affinity 
            // for each async continuation).
            // http://stackoverflow.com/a/14485163/505457
            return Task.Run(() => BulkQueryInternal(databases, query));
        }

        private static async Task<QueryResult> BulkQueryInternal(IList<DatabaseDefinition> databases, string query)
        {
            var resultsTasks = databases
                .Select(db => SingleQuery(db, query))
                .ToList();

            await Task.WhenAll(resultsTasks);
            var results = resultsTasks.Select(t => t.Result);

            DataTable aggregateResultTable = null;
            var messages = new List<string>();

            foreach (var result in results)
            {
                messages.AddRange(result.Messages);
                if (result.ResultTable == null)
                    continue;

                if (aggregateResultTable == null)
                {
                    // First set of results.. we'll use this as the 'schema' and all subsequent results must match it.
                    aggregateResultTable = result.ResultTable;
                }
                else
                {
                    // Check that the 'schema' of this db's result matches the initial schema.
                    if (!AreColumnsIdentical(aggregateResultTable.Columns, result.ResultTable.Columns))
                    {
                        messages.Add($"Columns returned by {result.ResultTable.TableName} does not match those of {aggregateResultTable.TableName}.");
                    }
                    else
                    {
                        // Copy the rows over to the existing datatable.
                        foreach (var row in result.ResultTable.Rows.Cast<DataRow>())
                        {
                            aggregateResultTable.Rows.Add(row.ItemArray);
                        }
                    }
                }
            }

            return new QueryResult
            {
                Messages = messages,
                ResultTable = aggregateResultTable
            };
        }

        private static async Task<QueryResult> SingleQuery(DatabaseDefinition db, string query)
        {

            var result = new QueryResult
            {
                Messages = new List<string>()
            };

            //            var isDbNameUnique = databases.Count(d => d.DatabaseName == db.DatabaseName) == 1;
            //            var friendlyDbName = db.DatabaseName;
            //            if (!isDbNameUnique)
            //            {
            //                friendlyDbName += " - " + db.Server.DisplayName;
            //            }
            var friendlyDbName = $"{db.DatabaseName} - {db.Server.DisplayName}";


            var builder = new SqlConnectionStringBuilder(db.Server.ConnectionString)
            {
                InitialCatalog = db.DatabaseName
            };
            using (SqlConnection connection = new SqlConnection(builder.ConnectionString))
            {
                try
                {
                    Debug.WriteLine($"connecting to {friendlyDbName}");
                    await connection.OpenAsync();
                    var command = connection.CreateCommand();
                    command.CommandText = query;
                    Debug.WriteLine("querying " + friendlyDbName);
                    using (var dataReader = await command.ExecuteReaderAsync())
                    {
                        Debug.WriteLine($"reading results from {friendlyDbName}");
                        result.ResultTable = ReadTable(dataReader);
                        result.ResultTable.TableName = friendlyDbName;
                        Debug.WriteLine($"finished reading results from {friendlyDbName}");

                        // Add a column that shows which DB it came from.
                        var sourceCol = new DataColumn("Source Database", typeof(string));
                        result.ResultTable.Columns.Add(sourceCol);
                        foreach (var col in result.ResultTable.Columns.Cast<DataColumn>().ToList())
                        {
                            if (col != sourceCol)
                                col.SetOrdinal(col.Ordinal + 1);
                        }
                        sourceCol.SetOrdinal(0);

                        foreach (var row in result.ResultTable.Rows.Cast<DataRow>())
                        {
                            row[sourceCol] = friendlyDbName;
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Messages.Add($"Error on {friendlyDbName}: {ex.Message}");
                }
            }

            return result;
        }

        // The built in dataTable.Load(dataReader) method is very slow for large data sets.
        // This one is taken from http://stackoverflow.com/questions/18961938/populate-data-table-from-data-reader
        // and seems to be significantly faster.
        private static DataTable ReadTable(SqlDataReader dataReader)
        {
            var dtSchema = dataReader.GetSchemaTable();
            var dt = new DataTable();
            var listCols = new List<DataColumn>();

            if (dtSchema != null)
            {
                foreach (DataRow drow in dtSchema.Rows)
                {
                    string columnName = Convert.ToString(drow["ColumnName"]);
                    DataColumn column = new DataColumn(columnName, (Type)(drow["DataType"]))
                    {
                        AllowDBNull = (bool)drow["AllowDBNull"]
                    };
                    listCols.Add(column);
                    dt.Columns.Add(column);
                }
            }

            // Read rows from DataReader and populate the DataTable
            while (dataReader.Read())
            // this can also be made async, but it doesn't seem to result in a perf gain, and can sometimes
            // make the connections flaky (intermittent read failures with lots of parallel queries), so leaving
            // it as a blocking read for now.
            //while (await dataReader.ReadAsync())
            {
                DataRow dataRow = dt.NewRow();
                for (var i = 0; i < listCols.Count; i++)
                {
                    dataRow[listCols[i]] = dataReader[i];
                }
                dt.Rows.Add(dataRow);
            }
            return dt;
        }
    }
}
