using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Box.V2;
using Box.V2.Config;
using Box.V2.JWTAuth;
using Box.V2.Models;
using box_migration_automation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Linq;
using Serilog;

namespace boxaccountorchestration
{
    public static class BoxAccountOrchestration
    {
        /*
        
            Given an IU netid:

                Delete any collaborations this user has on IU-owned folders
                Delete any data owned by this user
                Reactive the user account (set status to 'active')
                Roll the user out of the enterprise
                Send user an email notifying them that their account has been reactivated.      
        
        
        */
        public class RequestParams
        { 
            public string UserId { get; set; } 
            public string ItemId { get; set; }  
            public string ItemType { get; set; }
            public string FolderId { get; set; } = "0";
            
        }
        private static Serilog.ILogger Logger = Common.CreateLogger();

        [FunctionName("BoxAccountOrchestration")]
        public static async Task RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context, HttpRequest req,           
            ExecutionContext ctx, Microsoft.Extensions.Logging.ILogger log)
        { 
            log.LogInformation($"Start 'BoxAccountOrchestration' Function....");           
            var data =  context.GetInput<RequestParams>();
            //var log = Common.GetLogger(ctx, req, data.UserId);
            //var boxClient = await CreateBoxClient(Logger, data.UserId); 
            try
            {
                await context.CallSubOrchestratorAsync("TraverseCollabs", data.UserId);
                //await context.CallSubOrchestratorAsync("DeleteUserData", client);
                await context.CallActivityAsync("ReactivateTheUserAccount", data.UserId);
                await context.CallActivityAsync("RollOutTheUser", data.UserId);  
            }
            catch(Exception ex)
            {
                log.LogInformation($"BoxAccountOrchestration - throws an exception {ex}");

            }
                    
        }  

        [FunctionName("BoxAccountOrchestration_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            Microsoft.Extensions.Logging.ILogger log)
        {
            // Function input comes from the request content.    
            log.LogInformation($"Start 'BoxAccountOrchestration_HttpStart' Function...."); 
            var param = await req.Content.ReadAsAsync<RequestParams>();
            log.LogInformation($"params {param.UserId}, {param.FolderId}");
            string instanceId = await starter.StartNewAsync("BoxAccountOrchestration", param.UserId);

            log.LogInformation($"Start orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
        
        [FunctionName("RollOutTheUser")]
        public static async Task RollOutTheUser([ActivityTrigger] IDurableActivityContext context, Microsoft.Extensions.Logging.ILogger log)
        {
            log.LogInformation($"Start 'RollOutTheUser' Function...."); 
            var data =  context.GetInput<RequestParams>();
            var boxClient = await CreateBoxClient(Logger, data.UserId); 
            
            await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
            {
                Id = data.UserId,
                Enterprise = null,                
            });

            log.LogInformation($"Rolled out the user {data.UserId} from IU Enterprise");
        }
        
        [FunctionName("ReactivateTheUserAccount")]
        public static async Task ReactivateTheUserAccount([ActivityTrigger] IDurableActivityContext context, Microsoft.Extensions.Logging.ILogger log )
        {
            log.LogInformation($"Start 'ReactivateTheUserAccount' Function...."); 
            var data =  context.GetInput<RequestParams>();
            var boxClient = await CreateBoxClient(Logger, data.UserId); 
            
            await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
            {
                Id = data.UserId,
                Status = "active"
            });   

            log.LogInformation($"Reactivated the user account {data.UserId}.");        
        }

       // [FunctionName("RemoveCollabs")]
        // public static async Task RemoveCollabs([ActivityTrigger] IDurableActivityContext context, BoxCollaboration c, Microsoft.Extensions.Logging.ILogger log)
        // {
        //     var data =  context.GetInput<RequestParams>();
        //     var boxClient = await CreateBoxClient(Logger, data.UserId); 
           
        //     await boxClient.CollaborationsManager.RemoveCollaborationAsync(collab.Id);

        // }

        [FunctionName("TraverseCollabs")]
        public static async Task TraverseCollbs(
            [OrchestrationTrigger] IDurableOrchestrationContext context,  Microsoft.Extensions.Logging.ILogger log)
        {
            log.LogInformation($"Start 'TraverseCollabs' Function....");
            var data = context.GetInput<RequestParams>();
            var boxClient = await CreateBoxClient(Logger, data.UserId);
            var enterpriseUsers = GetUser(data, boxClient);

            var existingRootCollabs = await boxClient.FoldersManager.GetCollaborationsAsync(data.FolderId);

            var rootFolderCollabs = existingRootCollabs.Entries
                            .Where(c => c.CreatedBy.Login == data.UserId && enterpriseUsers.Result.Entries.Any(u => u.Enterprise.Name == c.CreatedBy.Enterprise.Name))
                            .Where(c => c.AccessibleBy != null && c.AccessibleBy.Id == data.UserId);

            var collabRemoveTasks = new List<Task>();
            foreach (var collab in rootFolderCollabs)
            {
                var removeTask = RemoveCollabs(boxClient, collab);
                collabRemoveTasks.Add(removeTask);
            }
            await Task.WhenAll(collabRemoveTasks);
            log.LogInformation($"Remove the collaborations for account {data.UserId}.");
        }

        private static Task<BoxCollection<BoxUser>> GetUser(RequestParams data, BoxClient boxClient)
        {
            var enterpriseUsers = boxClient.UsersManager.GetEnterpriseUsersAsync(filterTerm: data.UserId, fields: new[] { "name", "login" });
            if (enterpriseUsers.Result.Entries.Count > 1)
            {
                throw new Exception($"GetUser - found more than one user based on a search for '{data.UserId}'");
            }
            else if (enterpriseUsers.Result.Entries.Count == 0)
            {
                throw new Exception($"GetUser - No user found based on search for '{data.UserId}");
            }

            return enterpriseUsers;
        }

        private static async Task<BoxClient> CreateBoxClient (Serilog.ILogger log, string UserId) => await Common.GetBoxUserClient(log, UserId);
        private static Task RemoveCollabs(BoxClient sourceClient, BoxCollaboration c)
        {
           return sourceClient.CollaborationsManager.RemoveCollaborationAsync(c.Id);             
        }
        private static async Task<List<BoxCollaboration>> GetFolderCollaborators(BoxClient client, string itemId)
        {
            var collection = await client.FoldersManager.GetCollaborationsAsync(itemId);
            return collection.Entries;
        }           
        private static async Task<List<BoxCollaboration>> GetFileCollaborators(BoxClient client, string itemId)
        {
            var collection = await client.FilesManager.GetCollaborationsCollectionAsync(itemId, autoPaginate: true);
            return collection.Entries;
        }
        private static async Task ReactivateTheUserAccount(string boxUserId, BoxClient boxClient)
        {
            await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
            {
                Id = boxUserId,
                Status = "active"
            });
        }
        private static async Task RollOutTheUser(string boxUserId, BoxClient boxClient)
        {
            await boxClient.UsersManager.DeleteEnterpriseUserAsync(boxUserId, true, false);
        }
        private static async Task DeleteUserData(string fileId, string folderId, BoxClient boxClient)
        {
            var data =  new RequestParams();
            var deletedata =  data.ItemType == "file"
                    ? await boxClient.FilesManager.DeleteAsync(id: fileId )
                    : await boxClient.FoldersManager.DeleteAsync(id: folderId, recursive: true);
        }

    }
}