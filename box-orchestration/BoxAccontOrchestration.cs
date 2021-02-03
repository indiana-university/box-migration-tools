using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Box.V2;
using Box.V2.Config;
using Box.V2.JWTAuth;
using Box.V2.Models;
using System;
using System.Linq;
using Dasync.Collections;
using Microsoft.Extensions.Options;

namespace boxaccountorchestration
{
    public class BoxAccountOrchestration
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
            public string FolderId { get; set; } = "0";        
        }
        public class CollabParams
        { 
            public string UserId { get; set; } 
            public string CollabId { get; set; }        
        }
        
        public class Folder
        { 
            public string Id { get; set; } 
        }
        public class ResponseParams 
        {
            public string UserId { get; set; } 

            public Folder[] Folders { get; set; }
        }
        private readonly IBoxAccountConfig _config;

        public BoxAccountOrchestration(IOptions<BoxAccountConfig> config)
        {
            _config = config.Value;
        }        

        [FunctionName("BoxAccountOrchestration")]
        public static async Task RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,            
            ILogger log)
        { 
            log.LogInformation($"Start 'BoxAccountOrchestration' Function....");           
            var data =  context.GetInput<RequestParams>();             
            try
            {
                log.LogInformation($"Calling 'TraverseCollabs' Function...."); 
                await context.CallSubOrchestratorAsync("TraverseCollabs", data);
                log.LogInformation($"Calling 'DeleteUserData' Function...."); 
                //await context.CallActivityAsync("DeleteUserData", data);
                log.LogInformation($"Calling 'ReactivateTheUserAccount' Function...."); 
               // await context.CallActivityAsync("ReactivateTheUserAccount", data);
                log.LogInformation($"Calling 'RollOutTheUser' Function...."); 
                //await context.CallActivityAsync("RollOutTheUser", data);
                log.LogInformation($"Calling 'SendEmail' Function...."); 
               // await context.CallActivityAsync(nameof(SendEmail), data);

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
            ILogger log)
        {
            // Function input comes from the request content.    
            log.LogInformation($"Start 'BoxAccountOrchestration_HttpStart' Function...."); 
            var data = await req.Content.ReadAsAsync<RequestParams>();
            log.LogInformation($"params are {data.UserId}, {data.FolderId}");

            log.LogInformation($"Calling 'BoxAccountOrchestration' Function...."); 
            string instanceId = await starter.StartNewAsync("BoxAccountOrchestration", data);

            log.LogInformation($"Start orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        [FunctionName("CreateBoxAdminClient")]
        public BoxClient CreateBoxAdminClient(
            [OrchestrationTrigger] IDurableOrchestrationContext context,            
            ILogger log)
        { 
            var data = context.GetInput<RequestParams>();
            log.LogInformation($"Start 'CreateBoxAdminClient' Function and creating Box client...."); 
            var config = BoxConfig.CreateFromJsonString(_config.BoxConfigJson);            
            var auth = new BoxJWTAuth(config);
            var userToken = auth.UserToken(data.UserId);
            return auth.UserClient(userToken, data.UserId);  
        } 

        [FunctionName("RemoveCollab")]
        public static async Task RemoveCollab([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            log.LogInformation($"Start 'RemoveCollab' Function...."); 
              var collabData =  context.GetInput<CollabParams>();     
            log.LogInformation($"Collab UserId is: {collabData.UserId}"); // UserId has value ?      
            var boxClient = CreateBoxClient(collabData.UserId);             
            await boxClient.CollaborationsManager.RemoveCollaborationAsync(collabData.CollabId);           
            log.LogInformation($"Removed Collaboration for {collabData.UserId}");
        }

        [FunctionName("RollOutTheUser")]
        public static async Task RollOutTheUser([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            log.LogInformation($"Start 'RollOutTheUser' Function...."); 
            var data =  context.GetInput<RequestParams>();
            var boxClient = CreateBoxClient(data.UserId); 
            await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
            {
                Id = data.UserId,
                Enterprise = null,                
            });

            log.LogInformation($"Rolled out the user {data.UserId} from IU Enterprise");
        }
        
        [FunctionName("ReactivateTheUserAccount")]
        public static async Task ReactivateTheUserAccount([ActivityTrigger] IDurableActivityContext context, ILogger log )
        {
            log.LogInformation($"Start 'ReactivateTheUserAccount' Function...."); 
            var data =  context.GetInput<RequestParams>();
            var boxClient = CreateBoxClient(data.UserId); 
            
            await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
            {
                Id = data.UserId,
                Status = "active"
            });   

            log.LogInformation($"Reactivated the user account {data.UserId}.");        
        }

       [FunctionName("DeleteUserData")]
        public static async Task DeleteUserData([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            log.LogInformation($"Start 'DeleteUserData' Function....");

            var data =  context.GetInput<RequestParams>();
            var boxClient = CreateBoxClient(data.UserId); 
            var enterpriseUsers = GetUser(data.UserId, boxClient);

            var existingRootCollabs = await GetFolderCollaborators(boxClient, data.FolderId);

            var rootFolderCollabs = existingRootCollabs.Entries
                            .Where(c => c.CreatedBy.Login == data.UserId && enterpriseUsers.Result.Entries.Any(u => u.Enterprise.Name == c.CreatedBy.Enterprise.Name)) //IU Owned
                            .Where(c => c.AccessibleBy != null && c.AccessibleBy.Id == data.UserId) // Owner
                            .Select(c => DeleteData(c.Item, boxClient))
                            .ToList();
            log.LogInformation($"Deleted the User Data ....");
        }

        [FunctionName("TraverseCollabs")]
        public static async Task TraverseCollbs(
            [OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            log.LogInformation($"Start 'TraverseCollabs' Function....");          
            var data = context.GetInput<RequestParams>();
            log.LogInformation($"The Request Params values are: {data.UserId}, {data.FolderId}");
            var boxClient = await context.CallSubOrchestratorAsync<BoxClient>("CreateBoxAdminClient", data);
            var enterpriseUsers = GetUser(data.UserId, boxClient);
            var userLogin = enterpriseUsers.Result.Entries.Select(u=>u.Login).FirstOrDefault();           

            log.LogInformation($"The User {userLogin} found"); 
            var currentUser = await boxClient.UsersManager.GetCurrentUserInformationAsync();  //To know the current user
            log.LogInformation($"The current user login is {currentUser.Login} and {currentUser.Name}");             

            try
            {
                if (data.FolderId != "0")
                {
                    var existingCollabs = await GetFolderCollaborators(boxClient, data.FolderId); //this is just to test a single folder.
                    log.LogInformation($"Got the existing collabs and started removing task: for {currentUser.Login}");
                    //await RemoveCollaborationTask(context, log, data, boxClient, enterpriseUsers, existingCollabs);
                }
                else
                {   try 
                    {                
                        var subfolders = await context.CallSubOrchestratorAsync<List<Folder>>("ListSubFolders", data);  
                        var existingFolderCollabs = await context.CallSubOrchestratorAsync<BoxCollection<BoxCollaboration>>("GetFolderCollaborators", subfolders);
                        //log.LogInformation($"The Sub Folders Count: {subfolders.Result.Count()}"); 
                        //log.LogInformation($"The Collabs Count: {collabs.Result.Entries.Count()} and {collabs.Result.TotalCount}"); 
                        log.LogInformation($"Got the existing collabs and started removing task: for {currentUser.Login}");

                        try
            {
                var folderCollabs = existingFolderCollabs.Entries
                            .Where(c => c.CreatedBy.Login == data.UserId && enterpriseUsers.Result.Entries.Any(u => u.Enterprise.Name == c.CreatedBy.Enterprise.Name))
                            .Where(c => c.AccessibleBy != null && c.AccessibleBy.Id == data.UserId);
                var collabRemoveTasks = new List<Task>();
                foreach (var collab in folderCollabs)
                {
                    log.LogInformation($"Ready to remove?");
                    //var removeTask = context.CallActivityAsync("RemoveCollab", collab.Id); //boxClient.CollaborationsManager.RemoveCollaborationAsync(collab.Id);
                    //collabRemoveTasks.Add(removeTask);
                }
                await Task.WhenAll(collabRemoveTasks);
                log.LogInformation($"Remove the collaborations for account {data.UserId}.");
            }
            catch (Exception ex)
            {
                log.LogInformation($"The folder is not IU Owned: {ex}");
            }
                    }
                    catch(FunctionFailedException ex)
                    {
                        log.LogInformation($"ListSubFolders - Failed execution with error: {ex.Message} ");
                    }
                }
            }
            catch(Box.V2.Exceptions.BoxException ex){
                log.LogInformation($"{ex.Message}");
            }
        }

        [FunctionName("ListSubFolders")]
        public static async Task<Folder[]> GetSubfolders([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
            
        {
            log.LogInformation($"Start 'ListSubFolders' Function..........");

            var data = context.GetInput<RequestParams>();
            var boxClient = await context.CallSubOrchestratorAsync<BoxClient>("CreateBoxAdminClient", data);

            var items = await boxClient.FoldersManager.GetFolderItemsAsync(id: data.FolderId, limit: 1000, offset: 0, fields: new[] { "id", "owned_by" }, autoPaginate: true);            
            System.Console.WriteLine($"items count:{items.Entries.Count}");
            var subfolders = items.Entries
                .Where(i => i.Type == "folder")
                .Where(i => i.OwnedBy?.Id == data.UserId)                            
                .Select(f => new Folder { Id = f.Id})                             
                .ToArray();

           //var collabs = await context.CallActivityAsync<BoxCollection<BoxCollaboration>>("GetFolderCollaborators", subfolders);
            return subfolders;

        }
        
        [FunctionName("GetFolderCollaborators")]
        public static async Task<BoxCollection<BoxCollaboration>> GetFolderCollaborators([OrchestrationTrigger] IDurableOrchestrationContext context, ILogger log)
        {
            log.LogInformation($"Start 'GetFolderCollaborators' Function....");
            var data = context.GetInput<ResponseParams>();
            var boxClient = await context.CallSubOrchestratorAsync<BoxClient>("CreateBoxAdminClient", data);          
            BoxCollection<BoxCollaboration> collabs = new BoxCollection<BoxCollaboration>();
            foreach(var folder in data.Folders)
            {
                collabs = await boxClient.FoldersManager.GetCollaborationsAsync(folder.Id);//await GetFolderCollaborators(boxClient, folder.Id);
            }
            return collabs;
        }

        private static async Task RemoveCollaborationTask(IDurableOrchestrationContext context, ILogger log, RequestParams data, BoxClient boxClient, Task<BoxCollection<BoxUser>> enterpriseUsers, BoxCollection<BoxCollaboration> existingFolderCollabs)
        {
            try
            {
                var folderCollabs = existingFolderCollabs.Entries
                            .Where(c => c.CreatedBy.Login == data.UserId && enterpriseUsers.Result.Entries.Any(u => u.Enterprise.Name == c.CreatedBy.Enterprise.Name))
                            .Where(c => c.AccessibleBy != null && c.AccessibleBy.Id == data.UserId);
                var collabRemoveTasks = new List<Task>();
                foreach (var collab in folderCollabs)
                {
                    log.LogInformation($"Ready to remove?");
                    //var removeTask = context.CallActivityAsync("RemoveCollab", collab.Id); //boxClient.CollaborationsManager.RemoveCollaborationAsync(collab.Id);
                    //collabRemoveTasks.Add(removeTask);
                }
                await Task.WhenAll(collabRemoveTasks);
                log.LogInformation($"Remove the collaborations for account {data.UserId}.");
            }
            catch (Exception ex)
            {
                log.LogInformation($"The folder is not IU Owned: {ex}");
            }
        }

        private static Task<BoxCollection<BoxUser>> GetUser(string userId, BoxClient boxClient)
        {
            var enterpriseUsers = boxClient.UsersManager.GetEnterpriseUsersAsync(filterTerm: userId, fields: new[] { "name", "login" });
            if (enterpriseUsers.Result.Entries.Count > 1)
            {
                throw new Exception($"GetUser - found more than one user based on a search for '{userId}'");
            }
            else if (enterpriseUsers.Result.Entries.Count == 0)
            {
                throw new Exception($"GetUser - No user found based on search for '{userId}");
            }

            return enterpriseUsers;
        }

        private static async Task<Folder[]> GetSubfolders(BoxClient boxClient, string folderId, string userId)
        {
            var items = await boxClient.FoldersManager.GetFolderItemsAsync(id: folderId, limit: 1000, offset: 0, fields: new[] { "id", "name", "owned_by" }, autoPaginate: true);            
            System.Console.WriteLine($"items count:{items.Entries.Count}");
            var subfolders = items.Entries
                .Where(i => i.Type == "folder")
                .Where(i => i.OwnedBy?.Id == userId)                            
                .Select(f => new Folder { Id = f.Id})                             
                .ToArray();

            return subfolders;
        } 
        
        private static BoxClient CreateBoxClient (string UserId) 
        {
           
            var configJson = System.Environment.GetEnvironmentVariable("BoxConfigJson");                
            Console.WriteLine("Creating Box admin client...");
            var config = BoxConfig.CreateFromJsonString(configJson);
            var auth = new BoxJWTAuth(config);
            var adminToken = auth.AdminToken();
            return auth.AdminClient(adminToken); 
         
        }
        public static BoxClient CreateBoxUserClient( string userId)
        {
           
            var configJson = Env("BoxConfigJson");                
            Console.WriteLine("Creating Box user client...");
            var config = BoxConfig.CreateFromJsonString(configJson);
            var auth = new BoxJWTAuth(config);
            var userToken = auth.UserToken(userId);
            return auth.UserClient(userToken, userId);
            
        }
        public static string Env(string key)
            => string.IsNullOrWhiteSpace(key)
                    ? throw new Exception($"'{key}' is missing from the environment. This is a required setting.")
                    : System.Environment.GetEnvironmentVariable(key);

        private static async Task<BoxCollection<BoxCollaboration>> GetFolderCollaborators(BoxClient boxClient, string itemId)
            => await boxClient.FoldersManager.GetCollaborationsAsync(itemId); 
        private static async Task<bool> DeleteData(BoxItem item, BoxClient boxClient)
            => item.Type == "file"
                    ? await boxClient.FilesManager.DeleteAsync(id: item.Id )
                    : await boxClient.FoldersManager.DeleteAsync(id: item.Id, recursive: true);       

    }
}

