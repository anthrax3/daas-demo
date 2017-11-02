using Akka;
using Akka.Actor;
using HTTPlease;
using KubeNET.Swagger.Model;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Serialization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace DaaSDemo.Provisioning.Actors
{
    using Data;
    using Data.Models;
    using KubeClient;
    using Messages;

    /// <summary>
    ///     Actor that invokes a Kubernetes job to run SQLCMD.
    /// </summary>
    public class SqlRunner
        : ReceiveActorEx, IWithUnboundedStash
    {
        /// <summary>
        ///     The maximum amount of time to wait for a job to complete.
        /// </summary>
        public static readonly TimeSpan JobCompletionTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        ///     Cancellation for the periodic poll signal.
        /// </summary>
        ICancelable _pollCancellation;

        /// <summary>
        ///     Cancellation for the timeout signal.
        /// </summary>
        ICancelable _timeoutCancellation;

        /// <summary>
        ///     
        /// </summary>
        /// <param name="owner">
        ///     A reference to the actor that owns the <see cref="SqlRunner"/>.
        /// </param>
        /// <param name="serverId">
        ///     The Id of the target instance of SQL Server.
        /// </param>
        public SqlRunner(IActorRef owner, DatabaseServer server, string databaseName)
        {
            if (owner == null)
                throw new ArgumentNullException(nameof(owner));

            if (server == null)
                throw new ArgumentNullException(nameof(server));

            if (String.IsNullOrWhiteSpace(databaseName))
                throw new ArgumentException("Argument cannot be null, empty, or entirely composed of whitespace: 'databaseName'.", nameof(databaseName));

            Owner = owner;
            Server = server;
            DatabaseName = databaseName;
            KubeClient = CreateKubeApiClient();
        }

        /// <summary>
        ///     A reference to the actor that owns the <see cref="SqlRunner"/>.
        /// </summary>
        IActorRef Owner { get; }

        /// <summary>
        ///     A <see cref="DatabaseServer"/> representing the target instance of SQL Server.
        /// </summary>
        DatabaseServer Server { get; }

        /// <summary>
        ///     The <see cref="KubeApiClient"/> used to communicate with the Kubernetes API.
        /// </summary>
        KubeApiClient KubeClient { get; }

        /// <summary>
        ///     The name of the database targeted by the <see cref="SqlRunner"/>.
        /// </summary>
        string DatabaseName { get; }

        /// <summary>
        ///     The current state of the Job (if any) used to execute T-SQL.
        /// </summary>
        V1Job Job { get; set; }

        /// <summary>
        ///     The actor's local message-stash facility.
        /// </summary>
        public IStash Stash { get; set; }

        /// <summary>
        ///     Called when the actor is started.
        /// </summary>
        protected override void PreStart()
        {
            Become(Ready);
        }

        /// <summary>
        ///     Called when the actor has stopped.
        /// </summary>
        protected override void PostStop()
        {
            KubeClient.Dispose();

            base.PostStop();
        }

        /// <summary>
        ///     Called when the actor is ready to process requests.
        /// </summary>
        void Ready()
        {
            StopPolling();
            Stash.UnstashAll();

            Job = null;

            ReceiveAsync<ExecuteSql>(Execute);
        }

        /// <summary>
        ///     Called when the actor is executing a newly-created job.
        /// </summary>
        void ExecutingJob()
        {
            StartPolling();
            ReceiveAsync<Signal>(HandleCurrentJobSignal);
            Receive<ExecuteSql>(executeSql =>
            {
                // Defer request until existing job is complete.
                Stash.Stash();
            });
        }

        /// <summary>
        ///     Called when the actor is waiting for an existing job to finish.
        /// </summary>
        void WaitingForExistingJob()
        {
            StartPolling();

            ReceiveAsync<Signal>(HandleExistingJobSignal);
            Receive<ExecuteSql>(executeSql =>
            {
                // Defer request until existing job is complete.
                Stash.Stash();
            });
        }

        /// <summary>
        ///     Execute SQL.
        /// </summary>
        /// <param name="executeSql">
        ///     An <see cref="ExecuteSql"/> representing the T-SQL to execute.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation.
        /// </returns>
        async Task Execute(ExecuteSql executeSql)
        {
            if (executeSql == null)
                throw new ArgumentNullException(nameof(executeSql));

            V1Service serverService = await FindServerService();
            if (serverService == null)
            {
                Log.Error("Cannot find Service for server {ServerId}.", Server.Id);

                Owner.Tell(
                    new Status.Failure(new Exception(
                        message: $"Cannot find Service for server {Server.Id}."
                    ))
                );

                Context.Stop(Self);

                return;
            }

            Job = await FindJob(executeSql);
            if (Job != null)
            {
                Log.Info("Found existing job {JobName}.", Job.Metadata.Name);

                if (Job.Status.Active  == 0)
                {
                    Log.Info("Deleting existing job {JobName}...", Job.Metadata.Name);

                    await KubeClient.JobsV1.Delete(
                        Job.Metadata.Name
                    );

                    Log.Info("Deleted existing job {JobName}.", Job.Metadata.Name);
                }
                else
                {
                    Log.Info("Existing job {JobName} still has {ActivePodCount} active pods; will wait {JobCompletionTimeout} before forcing job termination...",
                        JobCompletionTimeout,
                        Job.Metadata.Name,
                        Job.Status.Active
                    );

                    // Existing job is running; wait for it to terminate.
                    Become(WaitingForExistingJob);

                    return;
                }
            }

            V1Secret secret = await EnsureSecretPresent(executeSql);

            V1ConfigMap configMap = await EnsureConfigMapPresent(executeSql, serverService);

            try
            {
                Job = await KubeClient.JobsV1.Create(new V1Job
                {
                    ApiVersion = "batch/v1",
                    Kind = "Job",
                    Metadata = new V1ObjectMeta
                    {
                        Name = GetJobName(executeSql),
                        Labels = new Dictionary<string, string>
                        {
                            ["cloud.dimensiondata.daas.server-id"] = Server.Id.ToString(),
                            ["cloud.dimensiondata.daas.database"] = DatabaseName
                        }
                    },
                    Spec = KubeSpecs.ExecuteSql(Server,
                        secretName: secret.Metadata.Name,
                        configMapName: configMap.Metadata.Name
                    )
                });

                Become(ExecutingJob);
            }
            catch (HttpRequestException<UnversionedStatus> createFailed)
            {
                Log.Error("Failed to create Job {JobName} for database {DatabaseName} in server {ServerId} (Message:{FailureMessage}, Reason:{FailureReason}).",
                    $"sqlcmd-{Server.Id}-{DatabaseName}",
                    DatabaseName,
                    Server.Id,
                    createFailed.Response.Message,
                    createFailed.Response.Reason
                );

                Owner.Tell(
                    new Status.Failure(createFailed)
                );

                Context.Stop(Self);
            }
        }

        /// <summary>
        ///     Handle a signal while waiting for a newly-created job to complete.
        /// </summary>
        /// <param name="signal">
        ///     The signal to handle.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation.
        /// </returns>
        async Task HandleCurrentJobSignal(Signal signal)
        {
            switch (signal)
            {
                case Signal.PollJobStatus:
                {
                    string jobName = Job.Metadata.Name;
                    
                    Job = await KubeClient.JobsV1.GetByName(jobName);
                    if (Job == null)
                    {
                        Log.Info("Job {JobName} not found; will treat as failed.", jobName);

                        Owner.Tell(
                            new SqlExecuted(jobName, Server.Id, DatabaseName, SqlExecutionResult.JobDeleted,
                                output: "T-SQL job was deleted."
                            )
                        );

                        Become(Ready);
                    }

                    if (Job.Status.Active == 0)
                    {
                        // TODO: This is a dodgy way to process the job's conditions. There can be multiple conditions.
                        V1JobCondition jobCondition = Job.Status.Conditions[0];
                        if (jobCondition.Type == "Complete")
                        {
                            Log.Info("Job {JobName} has successfully completed.",
                                Job.Metadata.Name
                            );

                            Owner.Tell(
                                new SqlExecuted(jobName, Server.Id, DatabaseName, SqlExecutionResult.Success,
                                    output: "T-SQL executed successfully." // TODO: Collect and use Pod logs here.
                                )
                            );
                        }
                        else
                        {
                            Log.Info("Job {JobName} failed ({FailureReason}: {FailureMessage}).",
                                Job.Metadata.Name,
                                jobCondition.Reason,
                                jobCondition.Message                                    
                            );

                            Owner.Tell(
                                new SqlExecuted(jobName, Server.Id, DatabaseName, SqlExecutionResult.Failed,
                                    output: $"Job {jobName} failed ({jobCondition.Reason}: {jobCondition.Message})."
                                )
                            );
                        }

                        Become(Ready);
                    }

                    break;
                }
                case Signal.Timeout:
                {
                    string jobName = Job.Metadata.Name;

                    Log.Info("Timed out after waiting {JobCompletionTimeout} for Job {JobName} to complete.", JobCompletionTimeout, jobName);
                    
                    Job = await KubeClient.JobsV1.GetByName(jobName);
                    if (Job != null)
                    {
                        Log.Info("Deleting Job {JobName}...", jobName);

                        await KubeClient.JobsV1.Delete(jobName);
                        Job = null;

                        Log.Info("Deleted Job {JobName}.", jobName);
                    }
                    else
                        Log.Info("Job {JobName} not found; will treat as completed.", jobName);

                    Owner.Tell(
                        new SqlExecuted(jobName, Server.Id, DatabaseName, SqlExecutionResult.JobTimeout,
                            output: "Timed out waiting for an existing T-SQL job to complete." // TODO: Collect this from pod logs.
                        )
                    );

                    Become(Ready);

                    break;
                }
                default:
                {
                    Unhandled(signal);

                    break;
                }
            }
        }

        /// <summary>
        ///     Handle a signal while waiting for an existing job to complete.
        /// </summary>
        /// <param name="signal">
        ///     The signal to handle.
        /// </param>
        /// <returns>
        ///     A <see cref="Task"/> representing the operation.
        /// </returns>
        async Task HandleExistingJobSignal(Signal signal)
        {
            switch (signal)
            {
                case Signal.PollJobStatus:
                {
                    string jobName = Job.Metadata.Name;
                    
                    Job = await KubeClient.JobsV1.GetByName(jobName);
                    if (Job == null)
                    {
                        Log.Info("Job {JobName} not found; will treat as completed.", jobName);

                        Become(ExecutingJob);
                    }

                    if (Job.Status.Active == 0)
                    {
                        if (Job.Status.Conditions[0].Type == "Complete")
                        {
                            Log.Info("Job {JobName} has successfully completed.",
                                Job.Metadata.Name
                            );
                        }
                        else
                        {
                            Log.Info("Job {JobName} failed ({FailureReason}:{FailureMessage}).",
                                Job.Metadata.Name,
                                Job.Status.Conditions[0].Reason,
                                Job.Status.Conditions[0].Message                                    
                            );
                        }

                        Become(ExecutingJob);
                    }

                    break;
                }
                case Signal.Timeout:
                {
                    string jobName = Job.Metadata.Name;
                    string databaseName;
                    Job.Metadata.Labels.TryGetValue("cloud.dimensiondata.daas.database", out databaseName);

                    Log.Info("Timed out after waiting {JobCompletionTimeout} for Job {JobName} to complete.", JobCompletionTimeout, jobName);
                    
                    Job = await KubeClient.JobsV1.GetByName(jobName);
                    if (Job != null)
                    {
                        Log.Info("Deleting Job {JobName}...", jobName);

                        await KubeClient.JobsV1.Delete(jobName);
                        Job = null;

                        Log.Info("Deleted Job {JobName}.", jobName);
                    }
                    else
                        Log.Info("Job {JobName} not found; will treat as completed.", jobName);

                    Owner.Tell(
                        new SqlExecuted(jobName, Server.Id, DatabaseName, SqlExecutionResult.JobTimeout,
                            output: "Timed out waiting for an existing T-SQL job to complete." // TODO: Collect this from pod logs.
                        )
                    );

                    Become(Ready);

                    break;
                }
                default:
                {
                    Unhandled(signal);

                    break;
                }
            }
        }

        /// <summary>
        ///     Start the polling and timeout signals.
        /// </summary>
        void StartPolling()
        {
            Log.Info("Starting the polling and timeout signals...");

            if (_pollCancellation != null || _timeoutCancellation != null)
            {
                Log.Warning("The polling and / or timeout signals are already active; cancelling...");
                
                StopPolling();
            }

            _pollCancellation = Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(
                initialDelay: TimeSpan.FromSeconds(5),
                interval: TimeSpan.FromSeconds(5),
                receiver: Self,
                message: Signal.PollJobStatus,
                sender: Self
            );

            _timeoutCancellation = Context.System.Scheduler.ScheduleTellOnceCancelable(
                delay: JobCompletionTimeout,
                receiver: Self,
                message: Signal.Timeout,
                sender: Self
            );

            Log.Info("The polling and timeout signals have been started.");
        }

        /// <summary>
        ///     Cancel the polling and timeout signals.
        /// </summary>
        void StopPolling()
        {
            if (_timeoutCancellation == null && _pollCancellation == null)
                return; // Nothing to do.

            Log.Info("Stopping the polling and / or timeout signals...");

            _timeoutCancellation?.Cancel();
            _timeoutCancellation = null;
            
            _pollCancellation?.Cancel();
            _pollCancellation = null;

            Log.Info("The polling and / or timeout have been stopped.");
        }

        /// <summary>
        ///     Find the current Job (if any) used to execute the specified T-SQL.
        /// </summary>
        /// <param name="executeSql">
        ///     The <see cref="ExecuteSql"/> message representing the T-SQL.
        /// </param>
        /// <returns>
        ///     The Job, or <c>null</c> if it was not found.
        /// </returns>
        async Task<V1Job> FindJob(ExecuteSql executeSql)
        {
            if (executeSql == null)
                throw new ArgumentNullException(nameof(executeSql));

            string jobName = GetJobName(executeSql);

            return await KubeClient.JobsV1.GetByName(jobName);
        }

        /// <summary>
        ///     Find the pod for the current Job (if any) used to execute the specified T-SQL.
        /// </summary>
        /// <param name="executeSql">
        ///     The <see cref="ExecuteSql"/> message representing the T-SQL.
        /// </param>
        /// <returns>
        ///     The Pod, or <c>null</c> if it was not found.
        /// </returns>
        async Task<V1Pod> FindJobPod(ExecuteSql executeSql)
        {
            if (executeSql == null)
                throw new ArgumentNullException(nameof(executeSql));
            
            string jobName = GetJobName(executeSql);

            List<V1Pod> matchingPods = await KubeClient.PodsV1.List(
                labelSelector: $"job-name = {jobName}"
            );
            if (matchingPods.Count == 0)
                return null;

            return matchingPods[matchingPods.Count - 1];
        }

        /// <summary>
        ///     Find the secret used to execute T-SQL in the specified database.
        /// </summary>
        /// <param name="executeSql">
        ///     An <see cref="ExecuteSql"/> message representing the T-SQL to execute.
        /// </param>
        /// <returns>
        ///     A <see cref="V1Secret"/> representing the secret, or <c>null</c>, if the secret was not found.
        /// </returns>
        async Task<V1Secret> FindSecret(ExecuteSql executeSql)
        {
            if (executeSql == null)
                throw new ArgumentNullException(nameof(executeSql));

            return await KubeClient.SecretsV1.GetByName(
                KubeResources.GetJobName(executeSql, Server, DatabaseName)
            );
        }

        /// <summary>
        ///     Ensure that the Kubernetes Secret exists for executing the specified T-SQL (creating it if necessary).
        /// </summary>
        /// <param name="executeSql">
        ///     An <see cref="ExecuteSql"/> message representing the T-SQL to execute.
        /// </param>
        /// <returns>
        ///     <c>true</c>, if the secret exists; otherwise, <c>false</c>.
        /// </returns>
        async Task<V1Secret> EnsureSecretPresent(ExecuteSql executeSql)
        {
            if (executeSql == null)
                throw new ArgumentNullException(nameof(executeSql));

            V1Secret existingSecret = await FindSecret(executeSql);
            if (existingSecret != null)
            {
                Log.Info("Found existing secret {SecretName} for database {DatabaseName} in server {ServerId}.",
                    existingSecret.Metadata.Name,
                    DatabaseName,
                    Server.Id
                );

                return existingSecret;
            }

            var newSecret = new V1Secret
            {
                ApiVersion = "v1",
                Kind = "Secret",
                Metadata = new V1ObjectMeta
                {
                    Name = KubeResources.GetJobName(executeSql, Server, DatabaseName),
                    Labels = new Dictionary<string, string>
                    {
                        ["cloud.dimensiondata.daas.server-id"] = Server.Id.ToString(),
                        ["cloud.dimensiondata.daas.database"] = DatabaseName,
                        ["cloud.dimensiondata.daas.action"] = "exec-sql"
                    }
                },
                Type = "Opaque",
                Data = new Dictionary<string, string>()
            };
            newSecret.AddData("database-user", "sa");
            newSecret.AddData("database-password", Server.AdminPassword);
            newSecret.AddData("secrets.sql", $@"
                :setvar DatabaseName '{DatabaseName}'
                :setvar DatabaseUser 'sa'
                :setvar DatabasePassword '{Server.AdminPassword}'
            ");

            V1Secret createdSecret;
            try
            {
                createdSecret = await KubeClient.SecretsV1.Create(newSecret);
            }
            catch (HttpRequestException<UnversionedStatus> createFailed)
            {
                Log.Error("Failed to create Secret {SecretName} for database {DatabaseName} in server {ServerId} (Message:{FailureMessage}, Reason:{FailureReason}).",
                    newSecret.Metadata.Name,
                    DatabaseName,
                    Server.Id,
                    createFailed.Response.Message,
                    createFailed.Response.Reason
                );

                throw;
            }

            Log.Info("Successfully created secret {SecretName} for database {DatabaseName} in server {ServerId}.",
                createdSecret.Metadata.Name,
                DatabaseName,
                Server.Id
            );

            return createdSecret;
        }

        /// <summary>
        ///     Ensure that the Kubernetes Secret does not exist for executing T-SQL in the specified database (deleting it if necessary).
        /// </summary>
        /// <param name="executeSql">
        ///     An <see cref="ExecuteSql"/> message representing the T-SQL to execute.
        /// </param>
        /// <returns>
        ///     <c>true</c>, if the secret does not exist; otherwise, <c>false</c>.
        /// </returns>
        async Task<bool> EnsureSecretAbsent(ExecuteSql executeSql)
        {
            if (executeSql == null)
                throw new ArgumentNullException(nameof(executeSql));
            
            V1Secret secret = await FindSecret(executeSql);
            if (secret == null)
                return true;

            Log.Info("Deleting secret {SecretName} for database {DatabaseName} in server {ServerId}...",
                secret.Metadata.Name,
                DatabaseName,
                Server.Id
            );

            try
            {
                await KubeClient.SecretsV1.Delete(
                    name: secret.Metadata.Name
                );
            }
            catch (HttpRequestException<UnversionedStatus> deleteFailed)
            {
                Log.Error("Failed to delete secret {SecretName} for database {DatabaseName} in server {ServerId} (Message:{FailureMessage}, Reason:{FailureReason}).",
                    secret.Metadata.Name,
                    DatabaseName,
                    Server.Id,
                    deleteFailed.Response.Message,
                    deleteFailed.Response.Reason
                );

                return false;
            }

            Log.Info("Deleted secret {SecretName} for database {DatabaseName} in server {ServerId}.",
                secret.Metadata.Name,
                DatabaseName,
                Server.Id
            );

            return true;
        }

        /// <summary>
        ///     Find the ConfigMap used to execute T-SQL in the specified database.
        /// </summary>
        /// <param name="executeSql">
        ///     An <see cref="ExecuteSql"/> message representing the T-SQL to execute.
        /// </param>
        /// <returns>
        ///     A <see cref="V1ConfigMap"/> representing the ConfigMap, or <c>null</c>, if the ConfigMap was not found.
        /// </returns>
        async Task<V1ConfigMap> FindConfigMap(ExecuteSql executeSql)
        {
            if (executeSql == null)
                throw new ArgumentNullException(nameof(executeSql));

            return await KubeClient.ConfigMapsV1.GetByName(
                name: KubeResources.GetJobName(executeSql, Server, DatabaseName)
            );
        }

        /// <summary>
        ///     Ensure that the Kubernetes ConfigMap exists for executing T-SQL in the specified database (creating it if necessary).
        /// </summary>
        /// <param name="executeSql">
        ///     An <see cref="ExecuteSql"/> message representing the T-SQL to execute.
        /// </param>
        /// <param name="serverService">
        ///     The Service used to communicate with the target instance of SQL Server.
        /// </param>
        /// <returns>
        ///     <c>true</c>, if the ConfigMap exists; otherwise, <c>false</c>.
        /// </returns>
        async Task<V1ConfigMap> EnsureConfigMapPresent(ExecuteSql executeSql, V1Service serverService)
        {
            if (executeSql == null)
                throw new ArgumentNullException(nameof(executeSql));

            if (serverService == null)
                throw new ArgumentNullException(nameof(serverService));

            V1ConfigMap existingConfigMap = await FindConfigMap(executeSql);
            if (existingConfigMap != null)
            {
                Log.Info("Found existing ConfigMap {ConfigMapName} for database {DatabaseName} in server {ServerId}.",
                    existingConfigMap.Metadata.Name,
                    DatabaseName,
                    Server.Id
                );

                return existingConfigMap;
            }

            var newConfigMap = new V1ConfigMap
            {
                ApiVersion = "v1",
                Kind = "ConfigMap",
                Metadata = new V1ObjectMeta
                {
                    Name = KubeResources.GetJobName(executeSql, Server, DatabaseName),
                    Labels = new Dictionary<string, string>
                    {
                        ["cloud.dimensiondata.daas.server-id"] = Server.Id.ToString(),
                        ["cloud.dimensiondata.daas.database"] = DatabaseName,
                        ["cloud.dimensiondata.daas.action"] = "exec-sql"
                    }
                },
                Data = new Dictionary<string, string>()
            };
            newConfigMap.AddData("database-server",
                value: $"{serverService.Metadata.Name}.{serverService.Metadata.Namespace}.svc.cluster.local,{serverService.Spec.Ports[0].Port}"
            );
            newConfigMap.AddData("database-name",
                value: DatabaseName
            );
            newConfigMap.AddData("script.sql",
                value: executeSql.Sql
            );

            V1ConfigMap createdConfigMap;
            try
            {
                createdConfigMap = await KubeClient.ConfigMapsV1.Create(newConfigMap);
            }
            catch (HttpRequestException<UnversionedStatus> createFailed)
            {
                Log.Error("Failed to create ConfigMap {ConfigMapName} for database {DatabaseName} in server {ServerId} (Message:{FailureMessage}, Reason:{FailureReason}).",
                    newConfigMap.Metadata.Name,
                    DatabaseName,
                    Server.Id,
                    createFailed.Response.Message,
                    createFailed.Response.Reason
                );

                throw;
            }

            Log.Info("Successfully created ConfigMap {ConfigMapName} for database {DatabaseName} in server {ServerId}.",
                createdConfigMap.Metadata.Name,
                DatabaseName,
                Server.Id
            );

            return createdConfigMap;
        }

        /// <summary>
        ///     Ensure that the Kubernetes ConfigMap does not exist for executing T-SQL in the specified database (deleting it if necessary).
        /// </summary>
        /// <param name="executeSql">
        ///     An <see cref="ExecuteSql"/> message representing the T-SQL to execute.
        /// </param>
        /// <returns>
        ///     <c>true</c>, if the ConfigMap does not exist; otherwise, <c>false</c>.
        /// </returns>
        async Task<bool> EnsureConfigMapAbsent(ExecuteSql executeSql)
        {
            if (executeSql == null)
                throw new ArgumentNullException(nameof(executeSql));

            V1ConfigMap configMap = await FindConfigMap(executeSql);
            if (configMap == null)
                return true;

            Log.Info("Deleting ConfigMap {ConfigMapName} for database {DatabaseName} in server {ServerId}...",
                configMap.Metadata.Name,
                DatabaseName,
                Server.Id
            );

            try
            {
                await KubeClient.ConfigMapsV1.Delete(
                    name: configMap.Metadata.Name
                );
            }
            catch (HttpRequestException<UnversionedStatus> deleteFailed)
            {
                Log.Error("Failed to delete ConfigMap {ConfigMapName} for database {DatabaseName} in server {ServerId} (Message:{FailureMessage}, Reason:{FailureReason}).",
                    configMap.Metadata.Name,
                    DatabaseName,
                    Server.Id,
                    deleteFailed.Response.Message,
                    deleteFailed.Response.Reason
                );

                return false;
            }

            Log.Info("Deleted ConfigMap {ConfigMapName} for database {DatabaseName} in server {ServerId}.",
                configMap.Metadata.Name,
                DatabaseName,
                Server.Id
            );

            return true;
        }

        /// <summary>
        ///     Find the server's associated Service (if it exists).
        /// </summary>
        /// <returns>
        ///     The Service, or <c>null</c> if it was not found.
        /// </returns>
        async Task<V1Service> FindServerService()
        {
            List<V1Service> matchingServices = await KubeClient.ServicesV1.List(
                kubeNamespace: "default",
                labelSelector: $"cloud.dimensiondata.daas.server-id = {Server.Id}"
            );

            return matchingServices[matchingServices.Count - 1];
        }

        /// <summary>
        ///     Create a new <see cref="KubeApiClient"/> for communicating with the Kubernetes API.
        /// </summary>
        /// <returns>
        ///     The configured <see cref="KubeApiClient"/>.
        /// </returns>
        KubeApiClient CreateKubeApiClient()
        {
            return KubeApiClient.Create(
                endPointUri: new Uri(
                    Context.System.Settings.Config.GetString("daas.kube.api-endpoint")
                ),
                accessToken: Context.System.Settings.Config.GetString("daas.kube.api-token")
            );
        }

        /// <summary>
        ///     Get the name of the job used to execute T-SQL.
        /// </summary>
        /// <param name="executeSql">
        ///     An <see cref="ExecuteSql"/> message representing the T-SQL to execute.
        /// </param>
        /// <returns>
        ///     The job name.
        /// </returns>
        string GetJobName(ExecuteSql executeSql) => $"sqlcmd-{Server.Id}-{DatabaseName}-{executeSql.JobNameSuffix}";

        /// <summary>
        ///     Well-known signals understood by the <see cref="SqlRunner"/> actor.
        /// </summary>
        enum Signal
        {
            /// <summary>
            ///     Poll the status of an existing job.
            /// </summary>
            PollJobStatus,

            /// <summary>
            ///     Terminate the polling of an existing job's status due to timeout.
            /// </summary>
            Timeout
        }
    }

    /// <summary>
    ///     Extension methods for Kubernetes model types.
    /// </summary>
    public static class KubeModelExtensions
    {
        /// <summary>
        ///     The default encoding (ASCII) used by these Kubernetes model extensions.
        /// </summary>
        public static readonly Encoding DefaultEncoding = Encoding.ASCII;

        /// <summary>
        ///     Add data to a ConfigMap.
        /// </summary>
        /// <param name="configMap">
        ///     The ConfigMap.
        /// </param>
        /// <param name="name">
        ///     The name of the data to add.
        /// </param>
        /// <param name="value">
        ///     The value to add.
        /// </param>
        /// <returns>
        ///     The ConfigMap (enables inline use / method-chaining).
        /// </returns>
        public static V1ConfigMap AddData(this V1ConfigMap configMap, string name, string value)
        {
            if (configMap == null)
                throw new ArgumentNullException(nameof(configMap));

            if (String.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Argument cannot be null, empty, or entirely composed of whitespace: 'name'.", nameof(name));

            if (value == null)
                throw new ArgumentNullException(nameof(value));

            if (configMap.Data == null)
                configMap.Data = new Dictionary<string, string>();

            configMap.Data.Add(name, value);

            return configMap;
        }

        /// <summary>
        ///     Add data to a Secret.
        /// </summary>
        /// <param name="secret">
        ///     The Secret.
        /// </param>
        /// <param name="name">
        ///     The name of the data to add.
        /// </param>
        /// <param name="value">
        ///     The value to add.
        /// </param>
        /// <param name="encoding">
        ///     An optional encoding to use (defaults to <see cref="DefaultEncoding"/>).
        /// </param>
        /// <returns>
        ///     The Secret (enables inline use / method-chaining).
        /// </returns>
        public static V1Secret AddData(this V1Secret secret, string name, string value, Encoding encoding = null)
        {
            if (secret == null)
                throw new ArgumentNullException(nameof(secret));

            if (String.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Argument cannot be null, empty, or entirely composed of whitespace: 'name'.", nameof(name));

            if (value == null)
                throw new ArgumentNullException(nameof(value));

            if (secret.Data == null)
                secret.Data = new Dictionary<string, string>();

            secret.Data.Add(name, Convert.ToBase64String(
                (encoding ?? DefaultEncoding).GetBytes(value)
            ));

            return secret;
        }
    }
}