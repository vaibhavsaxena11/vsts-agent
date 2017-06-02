using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Agent.Worker.Release;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.VisualStudio.Services.ReleaseManagement.WebApi.Contracts;
using Microsoft.VisualStudio.Services.Agent.Worker.Release.Artifacts;
using Microsoft.VisualStudio.Services.Agent.Worker.Release.Artifacts.Definition;
using Newtonsoft.Json;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Release
{
    public class ReleaseJobExtension : JobExtension
    {
        private const string DownloadArtifactsFailureSystemError = "DownloadArtifactsFailureSystemError";

        private const string DownloadArtifactsFailureUserError = "DownloadArtifactsFailureUserError";

        private static readonly Guid DownloadArtifactsTaskId = new Guid("B152FEAA-7E65-43C9-BCC4-07F6883EE793");

        private int ReleaseId { get; set; }
        private Guid TeamProjectId { get; set; }
        private string ArtifactsWorkingFolder { get; set; }
        private bool SkipArtifactsDownload { get; set; }

        public override Type ExtensionType => typeof(IJobExtension);
        public override HostTypes HostType => HostTypes.Release;

        public override IStep GetExtensionPreJobStep(IExecutionContext jobContext)
        {
            return new JobExtensionRunner(
                context: jobContext.CreateChild(Guid.NewGuid(), StringUtil.Loc("DownloadArtifacts"), nameof(ReleaseJobExtension)),
                runAsync: GetArtifactsAsync,
                condition: ExpressionManager.Succeeded,
                displayName: StringUtil.Loc("DownloadArtifacts"));
        }

        public override IStep GetExtensionPostJobStep(IExecutionContext jobContext)
        {
            return null;
        }

        public override string GetRootedPath(IExecutionContext context, string path)
        {
            string rootedPath = null;

            if (!string.IsNullOrEmpty(path) &&
                   path.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                   Path.IsPathRooted(path))
            {
                try
                {
                    rootedPath = Path.GetFullPath(path);
                    Trace.Info($"Path resolved by source provider is a rooted path, return absolute path: {rootedPath}");
                    return rootedPath;
                }
                catch (Exception ex)
                {
                    Trace.Info($"Path resolved is a rooted path, but it is not fully qualified, return the path: {path}");
                    Trace.Error(ex);
                    return path;
                }
            }

            string artifactRootPath = context.Variables.Release_ArtifactsDirectory ?? string.Empty;
            Trace.Info($"Artifact root path is system.artifactsDirectory: {artifactRootPath}");

            if (!string.IsNullOrEmpty(artifactRootPath) && artifactRootPath.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                path != null && path.IndexOfAny(Path.GetInvalidPathChars()) < 0)
            {
                path = Path.Combine(artifactRootPath, path);
                Trace.Info($"After prefix Artifact Path Root provide by JobExtension: {path}");
                if (Path.IsPathRooted(path))
                {
                    try
                    {
                        rootedPath = Path.GetFullPath(path);
                        Trace.Info($"Return absolute path after prefix ArtifactPathRoot: {rootedPath}");
                        return rootedPath;
                    }
                    catch (Exception ex)
                    {
                        Trace.Info($"After prefix Artifact Path Root provide by JobExtension. The Path is a rooted path, but it is not fully qualified, return the path: {path}");
                        Trace.Error(ex);
                        return path;
                    }
                }
            }

            return rootedPath;
        }

        public override void ConvertLocalPath(IExecutionContext context, string localPath, out string repoName, out string sourcePath)
        {
            Trace.Info($"Received localpath {localPath}");
            repoName = string.Empty;
            sourcePath = string.Empty;
        }

        private async Task GetArtifactsAsync(IExecutionContext executionContext)
        {
            Trace.Entering();

            if (SkipArtifactsDownload)
            {
                return;
            }

            try
            {
                // TODO: Create this as new task. Old windows agent does this. First is initialize which does the above and download task will be added based on skipDownloadArtifact option
                executionContext.Output("Downloading artifact");

                await DownloadArtifacts(executionContext, TeamProjectId, ArtifactsWorkingFolder, ReleaseId);
            }
            catch (Exception ex)
            {
                LogDownloadFailureTelemetry(executionContext, ex);
                throw;
            }
        }

        private async Task DownloadArtifacts(
            IExecutionContext executionContext,
            Guid teamProjectId,
            string artifactsWorkingFolder,
            int releaseId)
        {
            Trace.Entering();

            ServiceEndpoint vssEndpoint = executionContext.Endpoints.FirstOrDefault(e => string.Equals(e.Name, ServiceEndpoints.SystemVssConnection, StringComparison.OrdinalIgnoreCase));
            ArgUtil.NotNull(vssEndpoint, nameof(vssEndpoint));
            ArgUtil.NotNull(vssEndpoint.Url, nameof(vssEndpoint.Url));

            Trace.Info($"Connecting to {vssEndpoint.Url}/{teamProjectId}");
            var releaseServer = new ReleaseServer(vssEndpoint.Url, ApiUtil.GetVssCredential(vssEndpoint), teamProjectId);

            // TODO: send correct cancellation token
            List<AgentArtifactDefinition> releaseArtifacts = releaseServer.GetReleaseArtifactsFromService(releaseId).ToList();
            var filteredReleaseArtifacts = FilterArtifactDefintions(releaseArtifacts);
            filteredReleaseArtifacts.ToList().ForEach(x => Trace.Info($"Found Artifact = {x.Alias} of type {x.ArtifactType}"));

            CleanUpArtifactsFolder(executionContext, artifactsWorkingFolder);
            await DownloadArtifacts(executionContext, filteredReleaseArtifacts, artifactsWorkingFolder);
        }

        private async Task DownloadArtifacts(IExecutionContext executionContext, IList<AgentArtifactDefinition> agentArtifactDefinitions, string artifactsWorkingFolder)
        {
            Trace.Entering();

            foreach (AgentArtifactDefinition agentArtifactDefinition in agentArtifactDefinitions)
            {
                // We don't need to check if its old style artifact anymore. All the build data has been fixed and all the build artifact has Alias now.
                ArgUtil.NotNullOrEmpty(agentArtifactDefinition.Alias, nameof(agentArtifactDefinition.Alias));

                var extensionManager = HostContext.GetService<IExtensionManager>();
                IArtifactExtension extension = (extensionManager.GetExtensions<IArtifactExtension>()).FirstOrDefault(x => agentArtifactDefinition.ArtifactType == x.ArtifactType);

                if (extension == null)
                {
                    throw new InvalidOperationException(StringUtil.Loc("RMArtifactTypeNotSupported", agentArtifactDefinition.ArtifactType));
                }

                Trace.Info($"Found artifact extension of type {extension.ArtifactType}");
                executionContext.Output(StringUtil.Loc("RMStartArtifactsDownload"));
                ArtifactDefinition artifactDefinition = ConvertToArtifactDefinition(agentArtifactDefinition, executionContext, extension);
                executionContext.Output(StringUtil.Loc("RMArtifactDownloadBegin", agentArtifactDefinition.Alias, agentArtifactDefinition.ArtifactType));

                // Get the local path where this artifact should be downloaded. 
                string downloadFolderPath = Path.GetFullPath(Path.Combine(artifactsWorkingFolder, agentArtifactDefinition.Alias ?? string.Empty));

                // download the artifact to this path. 
                RetryExecutor retryExecutor = new RetryExecutor();
                retryExecutor.ShouldRetryAction = (ex) =>
                {
                    executionContext.Output(StringUtil.Loc("RMErrorDuringArtifactDownload", ex));

                    bool retry = true;
                    if (ex is ArtifactDownloadException)
                    {
                        retry = false;
                    }
                    else
                    {
                        executionContext.Output(StringUtil.Loc("RMRetryingArtifactDownload"));
                        Trace.Warning(ex.ToString());
                    }

                    return retry;
                };

                await retryExecutor.ExecuteAsync(
                    async () =>
                        {
                            var releaseFileSystemManager = HostContext.GetService<IReleaseFileSystemManager>();
                            executionContext.Output(StringUtil.Loc("RMEnsureArtifactFolderExistsAndIsClean", downloadFolderPath));
                            try
                            {
                                releaseFileSystemManager.CleanupDirectory(downloadFolderPath, executionContext.CancellationToken);
                            }
                            catch (Exception ex) when (ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
                            {
                                throw new ArtifactCleanupFailedException(StringUtil.Loc("FailedCleaningupRMArtifactDirectory", downloadFolderPath), ex);
                            }

                            await extension.DownloadAsync(executionContext, artifactDefinition, downloadFolderPath);
                        });

                executionContext.Output(StringUtil.Loc("RMArtifactDownloadFinished", agentArtifactDefinition.Alias));
            }

            executionContext.Output(StringUtil.Loc("RMArtifactsDownloadFinished"));
        }

        private void CleanUpArtifactsFolder(IExecutionContext executionContext, string artifactsWorkingFolder)
        {
            Trace.Entering();

            RetryExecutor retryExecutor = new RetryExecutor();
            retryExecutor.ShouldRetryAction = (ex) =>
            {
                executionContext.Output(StringUtil.Loc("RetryingRMArtifactCleanUp", artifactsWorkingFolder, ex));
                Trace.Error(ex);

                return true;
            };

            retryExecutor.Execute(
                () =>
                {
                    executionContext.Output(StringUtil.Loc("RMCleaningArtifactsDirectory", artifactsWorkingFolder));

                    try
                    {
                        if (Directory.Exists(artifactsWorkingFolder))
                        {
                            IOUtil.DeleteDirectory(artifactsWorkingFolder, executionContext.CancellationToken);
                        }

                        Directory.CreateDirectory(artifactsWorkingFolder);
                    }
                    catch (Exception ex) when (ex is DirectoryNotFoundException || ex is UnauthorizedAccessException)
                    {
                        throw new ArtifactCleanupFailedException(StringUtil.Loc("FailedCleaningupRMArtifactDirectory", artifactsWorkingFolder), ex);
                    }
                });

            executionContext.Output(StringUtil.Loc("RMCleanedUpArtifactsDirectory", artifactsWorkingFolder));
        }

        public override void InitializeJobExtension(IExecutionContext executionContext)
        {
            Trace.Entering();
            ArgUtil.NotNull(executionContext, nameof(executionContext));

            executionContext.Output(StringUtil.Loc("PrepareReleasesDir"));
            var directoryManager = HostContext.GetService<IReleaseDirectoryManager>();
            ReleaseId = executionContext.Variables.GetInt(Constants.Variables.Release.ReleaseId) ?? 0;
            TeamProjectId = executionContext.Variables.GetGuid(Constants.Variables.System.TeamProjectId) ?? Guid.Empty;
            SkipArtifactsDownload = executionContext.Variables.GetBoolean(Constants.Variables.Release.SkipArtifactsDownload) ?? false;
            string releaseDefinitionName = executionContext.Variables.Get(Constants.Variables.Release.ReleaseDefinitionName);

            // TODO: Should we also write to log in executionContext.Output methods? so that we don't have to repeat writing into logs?
            // Log these values here to debug scenarios where downloading the artifact fails.
            executionContext.Output($"ReleaseId={ReleaseId}, TeamProjectId={TeamProjectId}, ReleaseDefinitionName={releaseDefinitionName}");

            var releaseDefinition = executionContext.Variables.Get(Constants.Variables.Release.ReleaseDefinitionId);
            if (string.IsNullOrEmpty(releaseDefinition))
            {
                string pattern = new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars());
                Regex regex = new Regex(string.Format("[{0}]", Regex.Escape(pattern)));
                releaseDefinition = regex.Replace(releaseDefinitionName, string.Empty);
            }

            var releaseDefinitionToFolderMap = directoryManager.PrepareArtifactsDirectory(
                IOUtil.GetWorkPath(HostContext),
                executionContext.Variables.System_CollectionId,
                executionContext.Variables.System_TeamProjectId.ToString(),
                releaseDefinition);

            ArtifactsWorkingFolder = Path.Combine(
                IOUtil.GetWorkPath(HostContext),
                releaseDefinitionToFolderMap.ReleaseDirectory,
                Constants.Release.Path.ArtifactsDirectory);
            executionContext.Output($"Release folder: {ArtifactsWorkingFolder}");

            SetLocalVariables(executionContext, ArtifactsWorkingFolder);

            // Log the environment variables available after populating the variable service with our variables
            LogEnvironmentVariables(executionContext);

            if (SkipArtifactsDownload)
            {
                // If this is the first time the agent is executing a task, we need to create the artifactsFolder
                // otherwise Process.StartWithCreateProcess() will fail with the error "The directory name is invalid"
                // because the working folder doesn't exist
                CreateWorkingFolderIfRequired(executionContext, ArtifactsWorkingFolder);

                // log the message that the user chose to skip artifact download and move on
                executionContext.Output(StringUtil.Loc("RMUserChoseToSkipArtifactDownload"));
                Trace.Info("Skipping artifact download based on the setting specified.");
            }

        }

        private void SetLocalVariables(IExecutionContext executionContext, string artifactsDirectoryPath)
        {
            Trace.Entering();

            // Always set the AgentReleaseDirectory because this is set as the WorkingDirectory of the task.
            executionContext.Variables.Set(Constants.Variables.Release.AgentReleaseDirectory, artifactsDirectoryPath);

            // Set the ArtifactsDirectory even when artifacts downloaded is skipped. Reason: The task might want to access the old artifact.
            executionContext.Variables.Set(Constants.Variables.Release.ArtifactsDirectory, artifactsDirectoryPath);
            executionContext.Variables.Set(Constants.Variables.System.DefaultWorkingDirectory, artifactsDirectoryPath);
        }

        private void LogEnvironmentVariables(IExecutionContext executionContext)
        {
            Trace.Entering();
            string stringifiedEnvironmentVariables = AgentUtilities.GetPrintableEnvironmentVariables(executionContext.Variables.Public);

            // Use LogMessage to ensure that the logs reach the TWA UI, but don't spam the console cmd window
            executionContext.Output(StringUtil.Loc("RMEnvironmentVariablesAvailable", stringifiedEnvironmentVariables));
        }

        private void CreateWorkingFolderIfRequired(IExecutionContext executionContext, string artifactsFolderPath)
        {
            Trace.Entering();
            if (!Directory.Exists(artifactsFolderPath))
            {
                executionContext.Output($"Creating artifacts folder: {artifactsFolderPath}");
                Directory.CreateDirectory(artifactsFolderPath);
            }
        }

        private ArtifactDefinition ConvertToArtifactDefinition(AgentArtifactDefinition agentArtifactDefinition, IExecutionContext executionContext, IArtifactExtension extension)
        {
            Trace.Entering();

            ArgUtil.NotNull(agentArtifactDefinition, nameof(agentArtifactDefinition));
            ArgUtil.NotNull(executionContext, nameof(executionContext));

            var artifactDefinition = new ArtifactDefinition
            {
                ArtifactType = agentArtifactDefinition.ArtifactType,
                Name = agentArtifactDefinition.Name,
                Version = agentArtifactDefinition.Version
            };

            artifactDefinition.Details = extension.GetArtifactDetails(executionContext, agentArtifactDefinition);
            return artifactDefinition;
        }

        private void LogDownloadFailureTelemetry(IExecutionContext executionContext, Exception ex)
        {
            var code = (ex is Artifacts.ArtifactDownloadException || ex is ArtifactCleanupFailedException) ? DownloadArtifactsFailureUserError : DownloadArtifactsFailureSystemError;
            var issue = new Issue
            {
                Type = IssueType.Error,
                Message = StringUtil.Loc("DownloadArtifactsFailed", ex)
            };

            issue.Data.Add("AgentVersion", Constants.Agent.Version);
            issue.Data.Add("code", code);
            issue.Data.Add("TaskId", DownloadArtifactsTaskId.ToString());

            executionContext.AddIssue(issue);
        }

        private IList<AgentArtifactDefinition> FilterArtifactDefintions(IList<AgentArtifactDefinition> agentArtifactDefinitions)
        {
            var definitions = new List<AgentArtifactDefinition>();
            foreach (var agentArtifactDefinition in agentArtifactDefinitions)
            {
                if (agentArtifactDefinition.ArtifactType != AgentArtifactType.Custom)
                {
                    definitions.Add(agentArtifactDefinition);
                }
                else
                {
                    string artifactType = string.Empty;
                    var artifactDetails = JsonConvert.DeserializeObject<Dictionary<string, string>>(agentArtifactDefinition.Details);
                    if (artifactDetails.TryGetValue("ArtifactType", out artifactType))
                    {
                        if (artifactType == null || artifactType.Equals("Build", StringComparison.OrdinalIgnoreCase))
                        {
                            definitions.Add(agentArtifactDefinition);
                        }
                    }
                    else
                    {
                        definitions.Add(agentArtifactDefinition);
                    }
                }
            }

            return definitions;
        }
    }
}