﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Raven.Client.Documents.Session;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

using SqlClient = System.Data.SqlClient;

namespace DaaSDemo.DatabaseProxy.Controllers
{
    using Common.Options;
    using Data;
    using Models.Data;
    using KubeClient;
    using KubeClient.Models;
    using Models.DatabaseProxy;

    /// <summary>
    ///     Controller for the T-SQL proxy API.
    /// </summary>
    [Route("api/v1/sql")]
    public class SqlController
        : Controller
    {
        /// <summary>
        ///     The database Id representing the master database in any server.
        /// </summary>
        const string MasterDatabaseId = "master";

        /// <summary>
        ///     Create a new T-SQL proxy API controller.
        /// </summary>
        /// <param name="documentSession">
        ///     The RavenDB document session for the current request.
        /// </param>
        /// <param name="kubeClient">
        ///     The Kubernetes API client.
        /// </param>
        /// <param name="kubeOptions">
        ///     The application's Kubernetes options.
        /// </param>
        /// <param name="logger">
        ///     The controller's log facility.
        /// </param>
        public SqlController(IAsyncDocumentSession documentSession, KubeApiClient kubeClient, IOptions<KubernetesOptions> kubeOptions, ILogger<SqlController> logger)
        {
            if (documentSession == null)
                throw new ArgumentNullException(nameof(documentSession));

            if (kubeClient == null)
                throw new ArgumentNullException(nameof(kubeClient));

            if (kubeOptions == null)
                throw new ArgumentNullException(nameof(kubeOptions));

            if (logger == null)
                throw new ArgumentNullException(nameof(logger));
            
            DocumentSession = documentSession;
            KubeClient = kubeClient;
            KubeOptions = kubeOptions.Value;
            Log = logger;
        }

        /// <summary>
        ///     The RavenDB document session for the current request.
        /// </summary>
        IAsyncDocumentSession DocumentSession { get; }

        /// <summary>
        ///     The controller's log facility.
        /// </summary>
        ILogger Log { get; }

        /// <summary>
        ///     The Kubernetes API client.
        /// </summary>
        KubeApiClient KubeClient { get; }

        /// <summary>
        ///     The application's Kubernetes options.
        /// </summary>
        KubernetesOptions KubeOptions { get; }

        /// <summary>
        ///     Execute T-SQL as a command (i.e. non-query).
        /// </summary>
        /// <param name="command">
        ///     A <see cref="Command"/> from the request body, representing the T-SQL to execute.
        /// </param>
        [HttpPost("command")]
        public async Task<IActionResult> ExecuteCommand([FromBody] Command command)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var result = new CommandResult();

            string connectionString = await GetConnectionString(command);
            if (connectionString == null)
            {
                result.ResultCode = -1;
                result.Errors.Add(new SqlError
                {
                    Kind = SqlErrorKind.Infrastructure,
                    Message = $"Unable to determine connection settings for database {command.DatabaseId} in server {command.ServerId}."
                });

                return Ok(result);
            }

            using (SqlClient.SqlConnection sqlConnection = new SqlClient.SqlConnection(connectionString))
            {
                await sqlConnection.OpenAsync();

                sqlConnection.InfoMessage += (sender, args) =>
                {
                    result.Messages.Add(args.Message);
                };

                for (int batchIndex = 0; batchIndex < command.Sql.Count; batchIndex++)
                {
                    string sql = command.Sql[batchIndex];

                    Log.LogInformation("Executing T-SQL batch {BatchNumber} of {BatchCount}...",
                        batchIndex + 1,
                        command.Sql.Count
                    );

                    using (var sqlCommand = new SqlClient.SqlCommand(sql, sqlConnection))
                    {
                        sqlCommand.CommandType = CommandType.Text;

                        foreach (Parameter parameter in command.Parameters)
                        {
                            sqlCommand.Parameters.Add(
                                parameter.ToSqlParameter()
                            );
                        }

                        try
                        {
                            result.ResultCode = await sqlCommand.ExecuteNonQueryAsync();
                        }
                        catch (SqlClient.SqlException sqlException)
                        {
                            Log.LogError(sqlException, "Error while executing T-SQL: {ErrorMessage}", sqlException.Message);

                            result.ResultCode = -1;
                            result.Errors.AddRange(
                                sqlException.Errors.Cast<SqlClient.SqlError>().Select(
                                    error => new SqlError
                                    {
                                        Kind = SqlErrorKind.TSql,
                                        Message = error.Message,
                                        Class = error.Class,
                                        Number = error.Number,
                                        State = error.State,
                                        Procedure = error.Procedure,
                                        Source = error.Source,
                                        LineNumber = error.LineNumber
                                    }
                                )
                            );
                        }
                        catch (Exception unexpectedException)
                        {
                            Log.LogError(unexpectedException, "Unexpected error while executing T-SQL: {ErrorMessage}", unexpectedException.Message);

                            result.ResultCode = -1;
                            result.Errors.Add(new SqlError
                            {
                                Kind = SqlErrorKind.Infrastructure,
                                Message = $"Unexpected error while executing T-SQL: {unexpectedException.Message}"
                            });
                        }
                    }

                    Log.LogInformation("Executed T-SQL batch {BatchNumber} of {BatchCount}.",
                        batchIndex + 1,
                        command.Sql.Count
                    );
                }
            }

            return Ok(result);
        }

        /// <summary>
        ///     Execute T-SQL as a query.
        /// </summary>
        /// <param name="query">
        ///     A <see cref="Query"/> from the request body, representing the T-SQL to execute.
        /// </param>
        [HttpPost("query")]
        public async Task<IActionResult> ExecuteQuery([FromBody] Query query)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var queryResult = new QueryResult();

            string connectionString = await GetConnectionString(query);
            if (connectionString == null)
            {
                queryResult.ResultCode = -1;
                queryResult.Errors.Add(new SqlError
                {
                    Kind = SqlErrorKind.Infrastructure,
                    Message = $"Unable to determine connection settings for database {query.DatabaseId} in server {query.ServerId}."
                });

                return Ok(queryResult);
            }

            using (SqlClient.SqlConnection sqlConnection = new SqlClient.SqlConnection(connectionString))
            {
                await sqlConnection.OpenAsync();

                sqlConnection.InfoMessage += (sender, args) =>
                {
                    queryResult.Messages.Add(args.Message);
                };

                for (int batchIndex = 0; batchIndex < query.Sql.Count; batchIndex++)
                {
                    string sql = query.Sql[batchIndex];

                    Log.LogInformation("Executing T-SQL batch {BatchNumber} of {BatchCount}...",
                        batchIndex + 1,
                        query.Sql.Count
                    );

                    using (var sqlCommand = new SqlClient.SqlCommand(sql, sqlConnection))
                    {
                        sqlCommand.CommandType = CommandType.Text;

                        foreach (Parameter parameter in query.Parameters)
                        {
                            sqlCommand.Parameters.Add(
                                parameter.ToSqlParameter()
                            );
                        }

                        try
                        {
                            using (SqlClient.SqlDataReader reader = await sqlCommand.ExecuteReaderAsync())
                            {
                                await ReadResults(reader, queryResult);

                                while (await reader.NextResultAsync())
                                    await ReadResults(reader, queryResult);
                            }

                            queryResult.ResultCode = 0;
                        }
                        catch (SqlClient.SqlException sqlException)
                        {
                            Log.LogError(sqlException, "Error while executing T-SQL: {ErrorMessage}", sqlException.Message);

                            queryResult.ResultCode = -1;
                            queryResult.Errors.AddRange(
                                sqlException.Errors.Cast<SqlClient.SqlError>().Select(
                                    error => new SqlError
                                    {
                                        Kind = SqlErrorKind.TSql,
                                        Message = error.Message,
                                        Class = error.Class,
                                        Number = error.Number,
                                        State = error.State,
                                        Procedure = error.Procedure,
                                        Source = error.Source,
                                        LineNumber = error.LineNumber
                                    }
                                )
                            );
                        }
                        catch (Exception unexpectedException)
                        {
                            Log.LogError(unexpectedException, "Unexpected error while executing T-SQL: {ErrorMessage}", unexpectedException.Message);

                            queryResult.ResultCode = -1;
                            queryResult.Errors.Add(new SqlError
                            {
                                Kind = SqlErrorKind.Infrastructure,
                                Message = $"Unexpected error while executing T-SQL: {unexpectedException.Message}"
                            });
                        }
                    }

                    Log.LogInformation("Executed T-SQL batch {BatchNumber} of {BatchCount}.",
                        batchIndex + 1,
                        query.Sql.Count
                    );
                }
            }

            return Ok(queryResult);
        }

        /// <summary>
        ///     Determine the connection string for the specified <see cref="SqlRequest"/>.
        /// </summary>
        /// <param name="request">
        ///     The <see cref="SqlRequest"/> being executed.
        /// </param>
        /// <returns>
        ///     The connection string, or <c>null</c> if the connection string could not be determined.
        /// </returns>
        async Task<string> GetConnectionString(SqlRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            Log.LogInformation("Determining connection string for database {DatabaseId} in server {ServerId}...",
                request.DatabaseId,
                request.ServerId
            );

            DatabaseServer targetServer = await DocumentSession.LoadAsync<DatabaseServer>(request.ServerId);
            if (targetServer == null)
            {
                Log.LogWarning("Cannot determine connection string for database {DatabaseId} in server {ServerId} (server not found).",
                    request.DatabaseId,
                    request.ServerId
                );

                return null;
            }

            List<ServiceV1> matchingServices = await KubeClient.ServicesV1().List(
                labelSelector: $"cloud.dimensiondata.daas.server-id = {targetServer.Id},cloud.dimensiondata.daas.service-type = internal",
                kubeNamespace: KubeOptions.KubeNamespace
            );
            if (matchingServices.Count == 0)
            {
                Log.LogWarning("Cannot determine connection string for database {DatabaseId} in server {ServerId} (server's associated Kubernetes Service not found).",
                    request.DatabaseId,
                    request.ServerId
                );

                return null;
            }

            ServiceV1 serverService = matchingServices[matchingServices.Count - 1];
            string serverFQDN = $"{serverService.Metadata.Name}.{serverService.Metadata.Namespace}.svc.cluster.local";
            int serverPort = serverService.Spec.Ports[0].Port;

            Log.LogInformation("Database proxy will connect to SQL Server '{ServerFQDN}' on {ServerPort}.", serverFQDN, serverPort);

            var connectionStringBuilder = new SqlClient.SqlConnectionStringBuilder
            {
                DataSource = $"tcp:{serverFQDN},{serverPort}",
            };

            if (request.DatabaseId != MasterDatabaseId)
            {
                DatabaseInstance targetDatabase = await DocumentSession.LoadAsync<DatabaseInstance>(request.DatabaseId);
                if (targetDatabase == null)
                {
                    Log.LogWarning("Cannot determine connection string for database {DatabaseId} in server {ServerId} (database not found).",
                        request.DatabaseId,
                        request.ServerId
                    );

                    return null;
                }
                    
                connectionStringBuilder.InitialCatalog = targetDatabase.Name;

                if (request.ExecuteAsAdminUser)
                {
                    connectionStringBuilder.UserID = "sa";
                    connectionStringBuilder.Password = targetServer.AdminPassword;
                }
                else
                {
                    connectionStringBuilder.UserID = targetDatabase.DatabaseUser;
                    connectionStringBuilder.Password = targetDatabase.DatabasePassword;
                }
            }
            else
            {
                connectionStringBuilder.InitialCatalog = "master";
                
                connectionStringBuilder.UserID = "sa";
                connectionStringBuilder.Password = targetServer.AdminPassword;
            }

            Log.LogInformation("Successfully determined connection string for database {DatabaseId} ({DatabaseName}) in server {ServerId} ({ServerSqlName}).",
                request.DatabaseId,
                connectionStringBuilder.InitialCatalog,
                request.ServerId,
                connectionStringBuilder.DataSource
            );

            return connectionStringBuilder.ConnectionString;
        }

        /// <summary>
        ///     Populate a <see cref="QueryResult"/> with result-sets from the specified <see cref="SqlClient.SqlDataReader"/>.
        /// </summary>
        /// <param name="reader">
        ///     The <see cref="SqlClient.SqlDataReader"/> to read from.
        /// </param>
        /// <param name="queryResult">
        ///     The <see cref="QueryResult"/> to populate.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation.
        /// </returns>
        async Task ReadResults(SqlClient.SqlDataReader reader, QueryResult queryResult)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            
            if (queryResult == null)
                throw new ArgumentNullException(nameof(queryResult));
            
            ResultSet resultSet = new ResultSet();
            queryResult.ResultSets.Add(resultSet);
            while (await reader.ReadAsync())
            {
                var row = new ResultRow();
                resultSet.Rows.Add(row);

                for (int fieldIndex = 0; fieldIndex < reader.FieldCount; fieldIndex++)
                {
                    string fieldName = reader.GetName(fieldIndex);
                    if (!reader.IsDBNull(fieldIndex))
                    {
                        row.Columns[fieldName] = new JValue(
                            reader.GetValue(fieldIndex)
                        );
                    }
                    else
                        row.Columns[fieldName] = null;
                }
            }
        }
    }
}