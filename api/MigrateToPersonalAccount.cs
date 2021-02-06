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
using SendGrid.Helpers.Mail;
using SendGrid;
using Dasync.Collections;

namespace box_migration_automation
{
    public static class AccountMigration    
    {
        public class RequestParams
        { 
            // A Box Account login/email address
            public string UserEmail { get; set; }                 
        }

        private class MigrationParams : RequestParams
        {
            // a Box Account ID
            public string UserId { get; set; } 
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
       
        [FunctionName(nameof(MigrateToPersonalAccount))]
        public static async Task<HttpResponseMessage> MigrateToPersonalAccount(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.    
            var args = await req.Content.ReadAsAsync<RequestParams>();

            // resolve the username to a box account ID
            var result = await UserAccountId(args);

            // if the username was resolved start the orchestration and return http 202 accepted
            if (result.success)
            {
                log.LogInformation($"Resolved Box Account id {result.msg} for login {args.UserEmail}");
                var migrationParams = new MigrationParams(){UserId = result.msg, UserEmail=args.UserEmail};
                var instanceId = await starter.StartNewAsync(nameof(MigrationOrchestrator), migrationParams);
                return starter.CreateCheckStatusResponse(req, instanceId);
            }
            // if failed return a http 400 bad request with some error information.
            else
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(result.msg, System.Text.Encoding.UTF8, "text/plain"),
                };
            }
        }

        [FunctionName(nameof(MigrationOrchestrator))]
        public static async Task MigrationOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,            
            ILogger log)
        { 
            var args =  await Task.FromResult(context.GetInput<MigrationParams>());     

            //Generate list of collaborations to remove
            var itemsToProcess = await context.CallActivityAsync<IEnumerable<ItemParams>>(
                nameof(GetBoxItemsToProcess), args);

           // Fan-out to remove collaborations
            var itemTasks = itemsToProcess.Select(itemParams => 
                context.CallActivityAsync(nameof(ProcessItem), itemParams));

            // // Fan-in to await removal of collaborations
            await Task.WhenAll(itemTasks);

            // await context.CallActivityAsync(nameof(RollAccountOutOfEnterprise), args);
           // await context.CallActivityAsync(nameof(SendUserNotification), args);
        }  

        public static async Task<(bool success, string msg)> UserAccountId(RequestParams args)
        {
            var boxClient = CreateBoxAdminClient();
            var users = await boxClient.UsersManager.GetEnterpriseUsersAsync(filterTerm: args.UserEmail);
            if(users.Entries.Count > 1)
                return (false, $"More than one Box account found for {args.UserEmail}");
            else if(users.Entries.Count == 0)
                return (false, $"No Box account was found for {args.UserEmail}");
            else
                return (true, users.Entries.Single().Id);
        }

        [FunctionName(nameof(GetBoxItemsToProcess))]
        public static async Task<IEnumerable<ItemParams>> GetBoxItemsToProcess([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args = context.GetInput<MigrationParams>();

            // get a box client for args.UserId
            var boxClient = CreateBoxUserClient(args.UserId);

            // list items in account root
            var items = await boxClient.FoldersManager
                .GetFolderItemsAsync(id: "0", limit: 1000, offset: 0, fields: new[] { "id", "name", "owned_by", "is_externally_owned" }, autoPaginate: true);

            var ownedItems = ResolveOwnedItems(log, args, boxClient, items);
            var internalCollabs = await ResolveInternalCollaborations(log, args, boxClient, items);           
            return ownedItems.Concat(internalCollabs).OrderBy(i => i.ItemName);
        }

        private static IEnumerable<ItemParams> ResolveOwnedItems(ILogger log, MigrationParams args, BoxClient boxClient, BoxCollection<BoxItem> items)
            => items.Entries
                .Where(i => i.OwnedBy.Id == args.UserId)
                .Select(i => new ItemParams(args.UserId, i.Id, i.Type, i.Name));

        private static async Task<IEnumerable<ItemParams>> ResolveInternalCollaborations(ILogger log, MigrationParams args, BoxClient boxClient, BoxCollection<BoxItem> items)
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

            log.LogInformation($"interally collabed sub items count :{internallyCollabedItems.Count()}"); 

            // find collaborations for each item
            var itemsParams = new List<ItemParams>();
            foreach (var item in internallyCollabedItems)
            {
                // fetch all collaborations on this item
                //log.LogInformation($"Fetching collabs for {item.Type} {item.Name} (id: {item.Id}");
                var itemCollabs = item.Type == "file"
                        ? await GetFileCollaborators(boxClient, item.Id)
                        : await GetFolderCollaborators(boxClient, item.Id);

                // find the collaboration associated with this user.
                var userCollabs = itemCollabs
                    .Where(c => c.AccessibleBy.Id == args.UserId)
                    .Select(c => new ItemParams(args.UserId, c.Id, c.Type, item.Name))
                    .ToList();

                itemsParams.AddRange(userCollabs);
            }
            
            var interallyCollabedSubFolderCollabs = ResolveSubFolderCollaborations(log, boxClient, interallyCollabedFolders, args.UserId).Result;              
            log.LogInformation($"sub folder collabs count: {interallyCollabedSubFolderCollabs.Count()}");

            return itemsParams.Concat(interallyCollabedSubFolderCollabs);
        }
        private static async Task<IEnumerable<ItemParams>> ResolveSubFolderCollaborations(ILogger log, BoxClient boxClient, IEnumerable<BoxFolder> interallyCollabedFolders, string userId)
        {
            var folders = interallyCollabedFolders.Where(i => i.Type == "folder");
            var itemsParams = new List<ItemParams>();
            
            foreach (var folder in folders)
            {
               var subfolders = GetSubfolders(boxClient, folder.Id, userId).Result.ToList();
                                //.ParallelForEachAsync(f => GetSubfolders(boxClient, folder.Id, userId), 10);   
                foreach (var item in subfolders)
                {
                    // fetch all collaborations on this item
                    log.LogInformation($"Fetching collabs for {item.ItemType} {item.ItemName} (id: {item.ItemId}");
                    var itemCollabs = item.ItemType == "file"
                            ? await GetFileCollaborators(boxClient, item.ItemId)
                            : await GetFolderCollaborators(boxClient, item.ItemId);

                    // find the collaboration associated with this user.
                    var userCollabs = itemCollabs
                        .Where(c => c.AccessibleBy.Id == userId)
                        .Select(c => new ItemParams(userId, c.Id, c.Type, item.ItemName))
                        .ToList();

                    itemsParams.AddRange(userCollabs);
                     log.LogInformation($" Sub collabs for {item.ItemId} {item.ItemName}: {userCollabs.Count}");
                }                               
                              
            }  
            return itemsParams;          
           
        }
        private static async Task<ItemParams[]> GetSubfolders(BoxClient boxClient, string folderId, string userId)
        {
            var items = await boxClient.FoldersManager.GetFolderItemsAsync(id: folderId, limit: 1000, offset: 0, fields: new[] { "id", "name", "owned_by" }, autoPaginate: true);
            return items.Entries
                .Where(i => i.OwnedBy.Id != userId)
                .Select(f => new ItemParams(userId, f.Id, f.Type, f.Name))
                .Where(f => f.ItemType == "folder")
                .OrderBy(f => f.ItemName)
                .ToArray();
        }

        [FunctionName(nameof(ProcessItem))]
        public static async Task ProcessItem([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<ItemParams>();  
            var boxClient = CreateBoxUserClient(args.UserId);

            if (args.ItemType == "file")
            {
                log.LogInformation($"[{args.UserId}] Removing file {args.ItemName} (file id: {args.ItemId})");
                await boxClient.FilesManager.DeleteAsync(args.ItemId); 
                await boxClient.FilesManager.PurgeTrashedAsync(args.ItemId);
            }
            else if (args.ItemType == "folder")
            {
                log.LogInformation($"[{args.UserId}] Removing folder {args.ItemName} (folder id: {args.ItemId})");
                await boxClient.FoldersManager.DeleteAsync(args.ItemId, recursive: true);
                await boxClient.FoldersManager.PurgeTrashedFolderAsync(args.ItemId);
            }
            else if (args.ItemType == "collaboration")
            {
            
                try
                {
                    log.LogInformation($"[{args.UserId}] Removing internal collaboration on {args.ItemName} (collab id: {args.ItemId})");
                    await boxClient.CollaborationsManager.RemoveCollaborationAsync(args.ItemId);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, $"[{args.UserId}] Failed to remove internal collaboration on {args.ItemName} (collab id: {args.ItemId})");
                }
                
            }
            else
            {
                log.LogError($"[{args.UserId}] Unrecognized item type {args.ItemType} for {args.ItemName} (item id: {args.ItemId})");
            }
        }

        [FunctionName(nameof(RollAccountOutOfEnterprise))]
        public static async Task RollAccountOutOfEnterprise([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<MigrationParams>();
            // get a box admin client
            var boxClient = CreateBoxAdminClient();             
            // set user account as active
            log.LogInformation($"[{args.UserId}] Setting account status to 'active',");
            await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest() { Id = args.UserId, Status = "active" });  
            // roll the user out from the enterprise
            log.LogInformation($"[{args.UserId}] Rolling account out of enterprise.");
            await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest() { Id = args.UserId, Enterprise = null });  
        }
        
        [FunctionName(nameof(SendUserNotification))]
        public static async Task SendUserNotification([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<MigrationParams>();
            var apiKey = Environment.GetEnvironmentVariable("SendGridApiKey");
            var fromAddress = Environment.GetEnvironmentVariable("MigrationNotificationFromAddress");
            var client = new SendGridClient(apiKey);

            var message = new SendGridMessage();
            message.SetFrom(new EmailAddress(fromAddress, "Box Migration Notifications"));
            message.AddTo(new EmailAddress(args.UserEmail));
            message.SetSubject("Box account migration update");
            message.AddContent(MimeType.Text, $@"Hello, Your Box account has been migrated to a personal account. 
            Any external collaborations you may have had were preserved, but any university-related data has been removed from your account.");

            await client.SendEmailAsync(message);
        }

        public static BoxClient CreateBoxUserClient(string userId)
        {
            var auth = CreateBoxJwtAuth();
            var userToken = auth.UserToken(userId);
            return auth.UserClient(userToken, userId);  
        }

        public static BoxClient CreateBoxAdminClient()
        {
            var auth = CreateBoxJwtAuth();
            var adminToken = auth.AdminToken();
            return auth.AdminClient(adminToken);  
        }

        public static BoxJWTAuth CreateBoxJwtAuth()
        {
            var boxConfigJson = System.Environment.GetEnvironmentVariable("BoxConfigJson");
            var config = BoxConfig.CreateFromJsonString(boxConfigJson);            
            return new BoxJWTAuth(config);
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

