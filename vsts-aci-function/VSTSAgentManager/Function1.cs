using System;

using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Identity;
using Microsoft.VisualStudio.Services.WebApi;

namespace VSTSAgentManager
{
    public static class Function1
    {
        static Function1()

        {

            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

        }

        private static System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
            => args.Name.StartsWith("Microsoft.VisualStudio.Services.WebApi") ? typeof(IdentityDescriptor).Assembly : null;


        [FunctionName("StartVSTSBuildAgent")]
        public static async Task<HttpResponseMessage> StartVSTSBuildAgenttAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            var body = await GetBody(req);
            Task.Factory.StartNew(() => _StartVSTSBuildAgenttAsync(body, log)); // No await 

            return req.CreateResponse(HttpStatusCode.Created, "VSTS agent is starting...");
        }
        private static async Task _StartVSTSBuildAgenttAsync(RequestBody body, TraceWriter log)
        {
            var _azure = GetAzure();

            // vstsConnection.HttpClient.RaisePlanEventAsync(vstsConnection.ProjectId, "build", )
            var resourceGroupName = ConfigurationManager.AppSettings["ResourceGroupName"];
            var resourceGroup = await _azure.ResourceGroups.GetByNameAsync(resourceGroupName);
            var env = new Dictionary<string, string>
                 {
                    { "VSTS_ACCOUNT", ConfigurationManager.AppSettings["VSTS_AGENT_INPUT_URL"] },
                    { "VSTS_TOKEN", ConfigurationManager.AppSettings["VSTS_AGENT_INPUT_TOKEN"] },
                    { "VSTS_POOL", ConfigurationManager.AppSettings["VSTS_AGENT_INPUT_POOL"] },
                    { "VSTS_AGENT", body.Name }
                };
            try
            {
                var containerGroup = await _azure.ContainerGroups.Define(body.Name)
                    .WithRegion(resourceGroup.RegionName)
                    .WithExistingResourceGroup(resourceGroup)
                    .WithLinux()
                    .WithPrivateImageRegistry(
                        ConfigurationManager.AppSettings["RegistryUrl"],
                        ConfigurationManager.AppSettings["RegistryUser"],
                        ConfigurationManager.AppSettings["RegistryPassword"])
                    .WithoutVolume()
                    .DefineContainerInstance(body.Name)
                        .WithImage(ConfigurationManager.AppSettings["BuildServerImagePrefix"] + $"/{body.ProjectType}")
                        .WithExternalTcpPorts(443, 80)
                        .WithCpuCoreCount(2)
                        .WithMemorySizeInGB(3.5)
                        .WithEnvironmentVariables(env)
                        .Attach()
                    .CreateAsync();
            }
            catch (Exception ex)
            {
                log.Error("Failed to create", ex);
                throw;
            }
        }

        [FunctionName("StopVSTSBuildAgent")]
        public static async Task<HttpResponseMessage> StopVSTSBuildAgenttAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequestMessage req, TraceWriter log)
        {
            var _azure = GetAzure();
            await _azure.ContainerGroups.DeleteByResourceGroupAsync(ConfigurationManager.AppSettings["ResourceGroupName"], (await GetBody(req)).Name);
            return req.CreateResponse(HttpStatusCode.OK, "VSTS agent has been removed");
        }

        private static async Task<RequestBody> GetBody(HttpRequestMessage req)
          => await req.Content.ReadAsAsync<RequestBody>();


        private static string GetHeaderItem(this HttpRequestMessage req, string key)
            => req.GetQueryNameValuePairs()
                    .FirstOrDefault(q => string.Equals(q.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

        private static IAzure GetAzure()
        {
            var tenantId = ConfigurationManager.AppSettings["TenantId"];
            var sp = new ServicePrincipalLoginInformation
            {
                ClientId = ConfigurationManager.AppSettings["ClientId"],
                ClientSecret = ConfigurationManager.AppSettings["ClientSecret"]
            };
            return Azure.Authenticate(new AzureCredentials(sp, tenantId, AzureEnvironment.AzureGlobalCloud)).WithDefaultSubscription();
        }

    }
    public class RequestBody
    {
        public string Name { get; set; }
        public string PlanUrl { get; set; }
        public string HubName { get; set; }
        public string AuthToken { get; set; }
        public Guid ProjectId { get; set; }
        public Guid PlanId { get; set; }
        public Guid JobId { get; set; }
        public Guid TimelineId { get; set; }
        public Guid TaskInstanceId { get; set; }
        public string ProjectType { get; set; } // node, dotnetcore
    }
    public class VstsConnection : IDisposable
    {
        public VssConnection Connection { get; private set; }
        public Guid ProjectId { get; private set; }
        public string VstsUri { get; private set; }
        public Guid JobId { get; private set; }
        public Guid PlanId { get; private set; }
        public Guid TimelineId { get; private set; }
        public Guid TaskInstanceId { get; private set; }
        public string HubName { get; private set; }
        public TaskHttpClient HttpClient { get; private set; }
        public static VstsConnection GetConnection(RequestBody body)
        {
            var res = new VstsConnection(body);
            res.PrepareClient();
            return res;
        }
        private VstsConnection(RequestBody body)
        {

            Connection = new VssConnection(new Uri(body.PlanUrl), new VssBasicCredential("username", body.AuthToken));
            ProjectId = body.ProjectId;
            VstsUri = body.PlanUrl;
            JobId = body.JobId;
            PlanId = body.PlanId;
            TimelineId = body.TimelineId;
            TaskInstanceId = body.TaskInstanceId;
            HubName = body.HubName;
        }
        public void PrepareClient()
        {
            HttpClient = Connection.GetClient<TaskHttpClient>();
        }

        public void Dispose()
        {
            HttpClient?.Dispose();
            Connection?.Dispose();
        }

        public async Task WriteLogItem(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) throw new ArgumentNullException(nameof(line));
            if (HttpClient == null) throw new InvalidOperationException();

            await HttpClient.AppendTimelineRecordFeedAsync(ProjectId, HubName, PlanId, TimelineId, JobId, new List<string> { line });
        }

        public async Task RaiseCompletion(TaskResult taskResult = TaskResult.Succeeded)
            => await HttpClient.RaisePlanEventAsync(ProjectId, HubName, PlanId, new TaskCompletedEvent(JobId, TaskInstanceId, taskResult));

    }
}
