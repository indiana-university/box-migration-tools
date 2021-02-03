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
            public string UserEmail { get; set; }
            // public string FolderId { get; set; } = "0";        
        }
        public class ItemParams
        { 
            public string UserId { get; set; } 
            public string ItemId { get; set; }        
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

        [FunctionName("BoxAccountOrchestration_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.    
            log.LogInformation($"Start 'BoxAccountOrchestration_HttpStart' Function...."); 
            var data = await req.Content.ReadAsAsync<RequestParams>();

            log.LogInformation($"Calling 'BoxAccountOrchestration' Function...."); 
            string instanceId = await starter.StartNewAsync("BoxAccountOrchestration", data);

            log.LogInformation($"Start orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        [FunctionName("BoxAccountOrchestration")]
        public static async Task RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,            
            ILogger log)
        { 
            
            /* TODO
                fan-out activities to delete each collaboration
                activity to generate list of files to remove
                fan-out activities to delete each file
                activity to generate list of folders to remove
                fan-out activities to delete each folder
                activity to reactivate account
                activity to roll user out of enterprise
                activity to send email
            */

            /* Fan-out/Fan-in Pattern
            object[] workBatch = await context.CallActivityAsync<object[]>("F1", null);
            for (int i = 0; i < workBatch.Length; i++)
            {
                Task<int> task = context.CallActivityAsync<int>("F2", workBatch[i]);
                parallelTasks.Add(task);
            }

            await Task.WhenAll(parallelTasks);
            */
            var requestParams =  context.GetInput<RequestParams>();     

            // Generate list of collaborations to remove
            var listOfCollabsToRemove = await context.CallActivityAsync<IEnumerable<ItemParams>>(
                nameof(GetListOfCollaborationIdsToRemove), requestParams);

            // // Fan-out to remove collaborations
            // var collabRemovalTasks = listOfCollabsToRemove.Select(itemParams => 
            //     context.CallActivityAsync(nameof(RemoveCollaboration), itemParams));

            // // Fan-in to await removal of collaborations
            // await Task.WhenAll(collabRemovalTasks);

            // Generate list of folders to remove
            var listOfFoldersToRemove = await context.CallActivityAsync<IEnumerable<ItemParams>>(
                nameof(GetListOfFoldersToRemove), requestParams);

            // // Fan-out to remove folders
            // var folderRemovalTasks = listOfFoldersToRemove.Select(itemParams => 
            //     context.CallActivityAsync(nameof(RemoveFolder), itemParams));

            // // Fan-in to await removal of folders
            // await Task.WhenAll(folderRemovalTasks);

            // Generate list of files to remove
            var listOfFilesToRemove = await context.CallActivityAsync<IEnumerable<ItemParams>>(
                nameof(GetListOfFilesToRemove), requestParams);

            // // Fan-out to remove files
            // var fileRemovalTasks = listOfFilesToRemove.Select(itemParams => 
            //     context.CallActivityAsync(nameof(RemoveFile), itemParams));

            // // Fan-in to await removal of files
            // await Task.WhenAll(fileRemovalTasks);

            /*
            await context.CallActivityAsync(nameof(ActivateUserAccount), requestParams);
            await context.CallActivityAsync(nameof(RollUserOutOfEnterprise), requestParams);
            await context.CallActivityAsync(nameof(SendUserNotification), requestParams);
            */
            
        }  

        [FunctionName(nameof(GetListOfCollaborationIdsToRemove))]
        public async Task<IEnumerable<ItemParams>> GetListOfCollaborationIdsToRemove([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<RequestParams>();
            log.LogInformation($"args are:{args.UserId},{args.UserEmail}");
            
            // get a box client for args.UserId
            var boxClient = CreateBoxUserClient(args.UserId);
            var enterpriseUser = GetUser(args.UserId, boxClient);
            
            // list items in account root
            var items = await boxClient.FoldersManager.GetFolderItemsAsync(id: "0", limit: 1000, offset: 0, fields: new[] { "id", "owned_by" }, autoPaginate: true);            
            var listOfItems = items.Entries               
                .Where(i => i.OwnedBy.Login != args.UserId)                            
                .ToArray();
            
            log.LogInformation($"count of items:{listOfItems.Count()}");
            
            // find collaborations for each item
            var itemsParams = new List<ItemParams>();
            foreach(var item in listOfItems)
            {
               log.LogInformation($"reqs:{item.Id},{item.OwnedBy.Id},{item.OwnedBy.Login}");
                var existingCollabs = item.Type == "file" 
                        ? await GetFileCollaborators(boxClient, item.Id)
                        : await GetFolderCollaborators(boxClient, item.Id);

                // if the collaboration was created by an external user
                //   and the collaboration is accessible by this user
                // then add it to the list.
                log.LogInformation($"count of collabs:{existingCollabs.Count}");           
                var listOfCollabs = existingCollabs
                        .Where(c => c.Item != null && c.Item.Id == item.Id && c.Item.OwnedBy != item.OwnedBy)
                        .Where(c => c.Item.CreatedBy != item.CreatedBy &&  enterpriseUser.Result.Enterprise != c.Item.CreatedBy.Enterprise)
                        .Where(c => c.AccessibleBy != null && c.AccessibleBy.Id == args.UserId)
                        .Select(c => new ItemParams { ItemId = c.Item.Id, UserId = c.CreatedBy.Login}).ToList();
                
                itemsParams.AddRange(listOfCollabs);
            log.LogInformation($"count of collaborations:{listOfCollabs.Count}");

            }
            return itemsParams;
        }

        [FunctionName(nameof(RemoveCollaboration))]
        public async Task RemoveCollaboration([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<ItemParams>();  
            // get a box client for args.UserId
            var boxClient = CreateBoxUserClient(args.UserId);
            // remove collaboration with args.ItemId            
            await boxClient.CollaborationsManager.RemoveCollaborationAsync(args.ItemId);
        }

        [FunctionName(nameof(GetListOfFoldersToRemove))]
        public async Task<IEnumerable<ItemParams>> GetListOfFoldersToRemove([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<RequestParams>();     
            // get a box client for args.UserId
            var boxClient = CreateBoxUserClient(args.UserId);
            
            // list folders in account root
            var items = await boxClient.FoldersManager.GetFolderItemsAsync(id: "0", limit: 1000, offset: 0, fields: new[] { "id", "owned_by" }, autoPaginate: true);            
            var folders = items.Entries
                .Where(i => i.Type == "folder")
                .Where(i => i.OwnedBy.Login == args.UserId) 
                .ToArray();
            log.LogInformation($"count of folders:{folders.Count()}");
            
            // if folder is owned by the user, add it to list of items to remove
            var itemsParams = new List<ItemParams>(); 
            var listOfFolders = folders
                    .Select(f => new ItemParams { ItemId = f.Id, UserId = f.OwnedBy.Login})                  
                    .ToList();
            
            itemsParams.AddRange(listOfFolders);
            
            log.LogInformation($"count of folders:{listOfFolders.Count}");
            
            return itemsParams;
        }

        [FunctionName(nameof(RemoveFolder))]
        public async Task RemoveFolder([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<ItemParams>();             
            // get a box client for args.UserId
            var boxClient = CreateBoxUserClient(args.UserId);
            // remove folder with args.ItemId
            await boxClient.FoldersManager.DeleteAsync(args.ItemId, recursive: true);
        }

        
        [FunctionName(nameof(GetListOfFilesToRemove))]
        public async Task<IEnumerable<ItemParams>> GetListOfFilesToRemove([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<RequestParams>();     
            // get a box client for args.UserId
            var boxClient = CreateBoxUserClient(args.UserId);
           
            // list files in account root
            var items = await boxClient.FoldersManager.GetFolderItemsAsync(id: "0", limit: 1000, offset: 0, fields: new[] { "id", "owned_by" }, autoPaginate: true);            
            var files = items.Entries
                .Where(i => i.Type == "file")
                .Where(i => i.OwnedBy.Login == args.UserId) 
                .ToArray();

            // if files is owned by the user, add it to list of items to remove            
            var itemsParams = new List<ItemParams>();           
            var listOfFiles = files
                    .Select(f => new ItemParams { ItemId = f.Id, UserId = f.OwnedBy.Login})                  
                    .ToList();            
            itemsParams.AddRange(listOfFiles);
            log.LogInformation($"count of files:{listOfFiles.Count}");

            return itemsParams;
        }

        [FunctionName(nameof(RemoveFile))]
        public async Task RemoveFile([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
           var args =  context.GetInput<ItemParams>();             
            // get a box client for args.UserId
            var boxClient = CreateBoxUserClient(args.UserId);
            // remove file with args.ItemId
            await boxClient.FilesManager.DeleteAsync(id: args.ItemId); 
        }

        [FunctionName(nameof(ActivateUserAccount))]
        public async Task ActivateUserAccount([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<RequestParams>();
            // get a box admin client
            var boxClient = CreateBoxAdminClient(); 
            
            // set user account as active
            await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
            {
                Id = args.UserId,
                Status = "active"
            });  
        }

        [FunctionName(nameof(RollUserOutOfEnterprise))]
        public async Task RollUserOutOfEnterprise([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<RequestParams>();        
            // get a box admin client
            var boxClient = CreateBoxAdminClient();
            // set user enterprise to null
            await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
            {
                Id = args.UserId,
                Enterprise = null,                
            });

        }


        // [FunctionName(nameof(SendUserNotification))]
        // public async Task SendUserNotification([ActivityTrigger] IDurableActivityContext context, ILogger log)
        // {
        //     var args =  context.GetInput<RequestParams>();     
        //     // get a box admin client
        //     var boxClient = CreateBoxAdminClient();
        // }

        public BoxClient CreateBoxUserClient(string userId)
        {
            var config = BoxConfig.CreateFromJsonString(_config.BoxConfigJson);            
            var auth = new BoxJWTAuth(config);
            var userToken = auth.UserToken(userId);
            return auth.UserClient(userToken, userId);  
        }

        public BoxClient CreateBoxAdminClient()
        {
            var config = BoxConfig.CreateFromJsonString(_config.BoxConfigJson);            
            var auth = new BoxJWTAuth(config);
            var adminToken = auth.AdminToken();
            return auth.AdminClient(adminToken);  
        }
        private static Task<BoxUser> GetUser(string userId, BoxClient boxClient)
            => boxClient.UsersManager.GetCurrentUserInformationAsync(fields: new[] { "name", "login", "enterprise" });
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
    }
}

