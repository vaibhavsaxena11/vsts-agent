using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Services.Agent.Util;

namespace Microsoft.VisualStudio.Services.Agent.Worker.Docker
{
    [ServiceLocator(Default = typeof(DockerCommandManager))]
    public interface IDockerCommandManager : IAgentService
    {
        Task<int> DockerPull(IExecutionContext context, string image);
        Task<DockerInfo> DockerCreate(IExecutionContext context, string image);
        Task<int> DockerStart(IExecutionContext context, string containerId);
        Task<int> DockerStop(IExecutionContext context, string containerId);
        Task<int> DockerExec(IExecutionContext context, string containerId, string options, string command);
    }

    public class DockerCommandManager : AgentService, IDockerCommandManager
    {
        private string _dockerPath;

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);

            var whichUtil = HostContext.GetService<IWhichUtil>();
            _dockerPath = whichUtil.Which("docker", true);
        }

        public async Task<int> DockerPull(IExecutionContext context, string image)
        {
            return await ExecuteDockerCommandAsync(context, "pull", image, context.CancellationToken);
        }

        public async Task<DockerInfo> DockerCreate(IExecutionContext context, string image)
        {
            string dockerArgs = $"--name {context.Docker.ContainerName} --rm -v /var/run/docker.sock:/var/run/docker.sock -v {context.Variables.Agent_WorkFolder}:{context.Variables.Agent_WorkFolder} -v {IOUtil.GetExternalsPath()}:{IOUtil.GetExternalsPath()} {image} ping 127.0.0.1 -i 86400 -q";
            string containerId = (await ExecuteDockerCommandAsync(context, "create", dockerArgs)).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(containerId, nameof(DockerInfo.ContainerId));

            string username = (await ExecuteCommandAsync(context, "whoami", string.Empty)).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(username, nameof(DockerInfo.CurrentUserName));

            string uid = (await ExecuteCommandAsync(context, "id", $"-u {username}")).FirstOrDefault();
            ArgUtil.NotNullOrEmpty(uid, nameof(DockerInfo.CurrentUserId));

            DockerInfo docker = new DockerInfo()
            {
                ContainerName = context.Docker.ContainerName,
                ContainerId = containerId,
                CurrentUserName = username,
                CurrentUserId = uid,
            };

            return docker;
        }

        public async Task<int> DockerStart(IExecutionContext context, string containerId)
        {
            return await ExecuteDockerCommandAsync(context, "start", containerId, context.CancellationToken);
        }

        public async Task<int> DockerStop(IExecutionContext context, string containerId)
        {
            return await ExecuteDockerCommandAsync(context, "stop", containerId, context.CancellationToken);
        }

        public async Task<int> DockerExec(IExecutionContext context, string containerId, string options, string command)
        {
            return await ExecuteDockerCommandAsync(context, "exec", $"{options} {containerId} {command}", context.CancellationToken);
        }

        private async Task<int> ExecuteDockerCommandAsync(IExecutionContext context, string command, string options, CancellationToken cancellationToken = default(CancellationToken))
        {
            string arg = StringUtil.Format($"{command} {options}").Trim();
            context.Command($"{_dockerPath} {arg}");

            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                context.Output(message.Data);
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                context.Output(message.Data);
            };

            return await processInvoker.ExecuteAsync(
                workingDirectory: context.Variables.Agent_WorkFolder,
                fileName: _dockerPath,
                arguments: arg,
                environment: null,
                requireExitCodeZero: false,
                outputEncoding: null,
                cancellationToken: cancellationToken);
        }

        private async Task<List<string>> ExecuteDockerCommandAsync(IExecutionContext context, string command, string options)
        {
            string arg = StringUtil.Format($"{command} {options}").Trim();
            context.Command($"{_dockerPath} {arg}");

            List<string> output = new List<string>();
            object outputLock = new object();
            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            await processInvoker.ExecuteAsync(
                            workingDirectory: context.Variables.Agent_WorkFolder,
                            fileName: _dockerPath,
                            arguments: arg,
                            environment: null,
                            requireExitCodeZero: true,
                            outputEncoding: null,
                            cancellationToken: CancellationToken.None);

            return output;
        }

        private async Task<List<string>> ExecuteCommandAsync(IExecutionContext context, string command, string arg)
        {
            context.Command($"{command} {arg}");

            List<string> output = new List<string>();
            object outputLock = new object();
            var processInvoker = HostContext.CreateService<IProcessInvoker>();
            processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
            {
                if (!string.IsNullOrEmpty(message.Data))
                {
                    lock (outputLock)
                    {
                        output.Add(message.Data);
                    }
                }
            };

            await processInvoker.ExecuteAsync(
                            workingDirectory: context.Variables.Agent_WorkFolder,
                            fileName: command,
                            arguments: arg,
                            environment: null,
                            requireExitCodeZero: true,
                            outputEncoding: null,
                            cancellationToken: CancellationToken.None);

            return output;
        }
    }
}