namespace Microsoft.VisualStudio.Services.Agent.Worker.Docker
{
    public class DockerInfo
    {
        public string ContainerId { get; set; }
        public string ContainerName { get; set; }
        public string CurrentUserName { get; set; }
        public string CurrentUserId { get; set; }
    }
}