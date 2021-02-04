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
                 
        }
        public class ItemParams
        { 
            public ItemParams(){}
            public ItemParams(string userId, string itemId, string itemType, string itemName)
            {
                UserId = userId;
                ItemId = itemId;
                ItemType = itemType;
                ItemName = itemName;
            }
            public string UserId { get; set; } 
            public string ItemId { get; set; }    
            public string ItemType { get; set; }
            public string ItemName { get; set; } 
        }
        
        public class Folder
        { 
            public string Id { get; set; } 
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
            
            var requestParams =  context.GetInput<RequestParams>();     


            // Generate list of collaborations to remove
            var itemsToProcess = await context.CallActivityAsync<IEnumerable<ItemParams>>(
                nameof(GetBoxItemsToProcess), requestParams);

            foreach(var item in itemsToProcess)
            {
                log.LogInformation($"[{item.UserId}] Will remove {item.ItemType} {item.ItemName} ({item.ItemId})");
            }

            // // Fan-out to remove collaborations
            // var itemTasks = itemsToProcess.Select(itemParams => 
            //     context.CallActivityAsync(nameof(ProcessItem), itemParams));

            // // // Fan-in to await removal of collaborations
            // await Task.WhenAll(itemTasks);

            /*
            await context.CallActivityAsync(nameof(ActivateUserAccount), requestParams);
            await context.CallActivityAsync(nameof(RollUserOutOfEnterprise), requestParams);
            await context.CallActivityAsync(nameof(SendUserNotification), requestParams);
            */
            
        }  

        {
            {
            }
        }

        [FunctionName(nameof(GetBoxItemsToProcess))]
        public async Task<IEnumerable<ItemParams>> GetBoxItemsToProcess([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args = context.GetInput<RequestParams>();

            // get a box client for args.UserId
            var boxClient = CreateBoxUserClient(args.UserId);

            // list items in account root
            var items = await boxClient.FoldersManager
                .GetFolderItemsAsync(id: "0", limit: 1000, offset: 0, fields: new[] { "id", "name", "owned_by", "is_externally_owned" }, autoPaginate: true);

            var ownedItems = ResolveOwnedItems(log, args, boxClient, items);
            var internalCollabs = await ResolveInternalCollaborations(log, args, boxClient, items);
            return ownedItems.Concat(internalCollabs).OrderBy(i => i.ItemName);
        }

        private static IEnumerable<ItemParams> ResolveOwnedItems(ILogger log, RequestParams args, BoxClient boxClient, BoxCollection<BoxItem> items)
            => items.Entries
                .Where(i => i.OwnedBy.Id == args.UserId)
                .Select(i => new ItemParams(args.UserId, i.Id, i.Type, i.Name));

        private static async Task<IEnumerable<ItemParams>> ResolveInternalCollaborations(ILogger log, RequestParams args, BoxClient boxClient, BoxCollection<BoxItem> items)
        {
            // find items for which this user is part of an internal collaboration.
            var itemsWithDifferentOwner = items.Entries.Where(i => i.OwnedBy.Id != args.UserId);
            var interallyCollabedFolders = itemsWithDifferentOwner
                .Where(i => i.Type == "folder")
                .Cast<BoxFolder>()
                .Where(f => f.IsExternallyOwned.GetValueOrDefault(false) == false);
            var interallyCollabedFiles = itemsWithDifferentOwner
                .Where(i => i.Type == "file")
                .Cast<BoxFile>()
                .Where(f => f.IsExternallyOwned.GetValueOrDefault(false) == false);
            var internallyCollabedItems =
                interallyCollabedFiles.Cast<BoxItem>()
                .Concat(interallyCollabedFolders.Cast<BoxItem>())
                .OrderBy(i => i.Name);

            // find collaborations for each item
            var itemsParams = new List<ItemParams>();
            foreach (var item in internallyCollabedItems)
            {
                // fetch all collaborations on this item
                // log.LogInformation($"Fetching collabs for {item.Type} {item.Name} (id: {item.Id}");
                var itemCollabs = item.Type == "file"
                        ? await GetFileCollaborators(boxClient, item.Id)
                        : await GetFolderCollaborators(boxClient, item.Id);

                // find the collaboration associated with this user.
                var userCollabs = itemCollabs
                    .Where(c => c.AccessibleBy.Id == args.UserId)
                    .Select(c => new ItemParams(args.UserId, c.Id, c.Type, item.Name))
                    .ToList();

                log.LogInformation($"[{args.UserId}] Found {userCollabs.Count()} internal collabs for {item.Type} {item.Name} ({item.Id})");

                itemsParams.AddRange(userCollabs);
            }

            return itemsParams;
        }

        [FunctionName(nameof(ProcessItem))]
        public async Task ProcessItem([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<ItemParams>();  
            var boxClient = CreateBoxUserClient(args.UserId);

            if (args.ItemType == "file")
            {
                log.LogInformation($"[{args.UserId}] Removing file {args.ItemName} (file id: {args.ItemId})");
                // await boxClient.FilesManager.DeleteAsync(id: args.ItemId); 
            }
            else if (args.ItemType == "folder")
            {
                log.LogInformation($"[{args.UserId}] Removing folder {args.ItemName} (folder id: {args.ItemId})");
                // await boxClient.FoldersManager.DeleteAsync(args.ItemId, recursive: true);
            }
            else if (args.ItemType == "collaboration")
            {
                log.LogInformation($"[{args.UserId}] Removing internal collaboration on {args.ItemName} (collab id: {args.ItemId})");
                // await boxClient.CollaborationsManager.RemoveCollaborationAsync(args.ItemId);
            }
            else
            {
                log.LogError($"[{args.UserId}] Unrecognized item type {args.ItemType} for {args.ItemName} (item id: {args.ItemId})");
            }
        }

        // [FunctionName(nameof(ActivateUserAccount))]
        // public async Task ActivateUserAccount([ActivityTrigger] IDurableActivityContext context, ILogger log)
        // {
        //     var args =  context.GetInput<RequestParams>();
        //     // get a box admin client
        //     var boxClient = CreateBoxAdminClient(); 
            
        //     // set user account as active
        //     await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
        //     {
        //         Id = args.UserId,
        //         Status = "active"
        //     });  
        // }

        // [FunctionName(nameof(RollUserOutOfEnterprise))]
        // public async Task RollUserOutOfEnterprise([ActivityTrigger] IDurableActivityContext context, ILogger log)
        // {
        //     var args =  context.GetInput<RequestParams>();        
        //     // get a box admin client
        //     var boxClient = CreateBoxAdminClient();
        //     // set user enterprise to null
        //     await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
        //     {
        //         Id = args.UserId,
        //         Enterprise = null,                
        //     });
        // }


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

        private static async Task<List<BoxCollaboration>> GetFolderCollaborators(BoxClient client, string itemId)
        {
            var collection = await client.FoldersManager.GetCollaborationsAsync(itemId, fields:new[]{"owned_by", "accessible_by"});
            return collection.Entries;
        }

        private static async Task<List<BoxCollaboration>> GetFileCollaborators(BoxClient client, string itemId)
        {
            var collection = await client.FilesManager.GetCollaborationsCollectionAsync(itemId, fields:new[]{"owned_by", "accessible_by"}, autoPaginate: true);
            return collection.Entries;
        }
    }
}

