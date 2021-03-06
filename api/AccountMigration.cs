using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Box.V2;
using Box.V2.Models;
using System;
using System.Linq;
using Serilog;
using Microsoft.AspNetCore.Mvc;

namespace box_migration_automation
{
    public static class AccountMigration    
    {
        public class RequestParams
        { 
            // A Box Account login/email address
            public string UserEmail { get; set; }          
        }

        public class MigrationParams : RequestParams
        {
            // a Box Account ID
            public string UserId { get; set; } 
        }
        
        public class ItemParams : MigrationParams
        { 
            public ItemParams(){}
            public ItemParams(MigrationParams args, string itemId, string itemType, string itemName)
            {
                UserId = args.UserId;
                UserEmail = args.UserEmail;
                ItemId = itemId;
                ItemType = itemType;
                ItemName = itemName;  
            }
            public string ItemId { get; set; }    
            public string ItemType { get; set; }
            public string ItemName { get; set; } 
        }
       
        [FunctionName(nameof(MigrateToPersonalAccount))]
        public static async Task<IActionResult> MigrateToPersonalAccount(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] IDurableOrchestrationClient starter, 
            ExecutionContext ctx)
        {
            // Function input comes from the request content.    
            var args = await Common.DeserializeRequestBody<RequestParams>(req);
            var log = Common.GetLogger(ctx, req, null, args.UserEmail);

            // resolve the username to a box account ID
            var result = await UserAccountId(log, args);

            // if the username was resolved start the orchestration and return http 202 accepted
            if (result.success)
            {
                var migrationParams = new MigrationParams(){UserId = result.msg, UserEmail=args.UserEmail};
                var instanceId = await starter.StartNewAsync(nameof(MigrationOrchestrator), migrationParams);
                return starter.CreateCheckStatusResponse(req, instanceId);
            }
            // if failed return a http 400 bad request with some error information.
            else
            {
                return new BadRequestObjectResult(result.msg);
            }
        }

        [FunctionName(nameof(MigrationOrchestrator))]
        public static async Task MigrationOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var args = await Task.FromResult(context.GetInput<MigrationParams>());

            await ActivateAccount(context, args);
            await RemoveFilesFoldersAndInternalCollabs(context, args);
            await EmptyTrash(context, args);
            await ConvertToPersonalAccount(context, args);
        }
        private static readonly RetryOptions RetryOptions = new RetryOptions(
                                firstRetryInterval: TimeSpan.FromSeconds(5),
                                maxNumberOfAttempts: 3); 

        private static Task ActivateAccount(IDurableOrchestrationContext context, MigrationParams args)
            => context.CallActivityWithRetryAsync(nameof(SetAccountStatusToActive), RetryOptions, args);

        private static async Task RemoveFilesFoldersAndInternalCollabs(IDurableOrchestrationContext context, MigrationParams args)
        {

            /* Delete Files, Folders, and internal Collaborations */
            IEnumerable<ItemParams> itemsToProcess;
            int rounds = 0;
            do
            {
                // Generate list of collaborations to remove
                itemsToProcess = await context.CallActivityWithRetryAsync<IEnumerable<ItemParams>>(
                    nameof(GetBoxItemsToRemove), RetryOptions, args);
                foreach (var item in itemsToProcess)
                {
                    await context.CallActivityWithRetryAsync(nameof(RemoveItem), RetryOptions, item);
                } 

            } while (itemsToProcess.Count() != 0 && (rounds++) < 100);
        }

        private static async Task EmptyTrash(IDurableOrchestrationContext context, MigrationParams args)
        {
            var trashedItems = await context.CallActivityWithRetryAsync<IEnumerable<ItemParams>>(
                nameof(ListAllTheTrashedItems), RetryOptions, args);

            foreach (var item in trashedItems)
            {
                await context.CallActivityWithRetryAsync(nameof(PurgeTrashedItem), RetryOptions, item);
            }
        }

        private static async Task ConvertToPersonalAccount(IDurableOrchestrationContext context, MigrationParams args)
        {
            await context.CallActivityWithRetryAsync(nameof(SetPersonalAccountQuota), RetryOptions, args);
            await context.CallActivityWithRetryAsync(nameof(RollAccountOutOfEnterprise), RetryOptions, args);
        }

        public static async Task<(bool success, string msg)> UserAccountId(ILogger log, RequestParams args)
        {
            var boxClient = await Common.GetBoxAdminClient(log);
            var username = args.UserEmail.Split('@', StringSplitOptions.RemoveEmptyEntries).First();
            var users = await Query(log, () => boxClient.UsersManager.GetEnterpriseUsersAsync(filterTerm: $"{username}@"),
                $"Fetch Box account(s) for login.");

            var exactMatch = users.Entries.SingleOrDefault(e => e.Login.ToLowerInvariant().Equals(args.UserEmail.ToLowerInvariant()));
            if (exactMatch != null)
            {
                log.Information($"Found Box account {{{Constants.UserId}}} exactly matching login.", exactMatch.Id);
                return (true, exactMatch.Id);
            }    
            else if(users.Entries.Count > 1)
            {
                var logins = string.Join(", ", users.Entries.Select(e => e.Login));
                log.Warning($"Multiple Box accounts found for login: {logins}");
                return (false, $"Multiple Box accounts found for login: ");
            }
            else if(users.Entries.Count == 0)
            {
                log.Warning($"No Box account found for login.");
                return (false, $"No Box account was found for login.");
            }
            else
            {
                var user = users.Entries.Single();
                log.Information($"Found Box account {{{Constants.UserId}}} based on username match for login {user.Login}.", user.Id);
                return (true, user.Id);
            }
        }


        [FunctionName(nameof(GetBoxItemsToRemove))]
        public static async Task<IEnumerable<ItemParams>> GetBoxItemsToRemove([ActivityTrigger] IDurableActivityContext context, ExecutionContext ctx)
        {
            var args = context.GetInput<MigrationParams>();
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);

            // get a box client for args.UserId
            var boxClient = await Common.GetBoxUserClient(log, args.UserId);

            // list items in account root
            var items = await Query(log, ()=>GetFolderItems(boxClient), $"Get Folder Items");

            var ownedItems = ResolveOwnedItems(args, boxClient, items);
            var internalCollabs = await ResolveInternalCollaborations(log, args, boxClient, items);
            return ownedItems.Concat(internalCollabs).OrderBy(i => i.ItemName);
        }

        private static Task<BoxCollection<BoxItem>> GetFolderItems(BoxClient boxClient)
            => boxClient.FoldersManager
                        .GetFolderItemsAsync(id: "0", limit: 1000, offset: 0, fields: new[] { "id", "name", "owned_by", "is_externally_owned", "path_collection" }, autoPaginate: true);        


        private static IEnumerable<ItemParams> ResolveOwnedItems(MigrationParams args, BoxClient boxClient, BoxCollection<BoxItem> items)
            => items.Entries
                .Where(i => i.OwnedBy.Id == args.UserId)
                .Select(i => new ItemParams(args, i.Id, i.Type, i.Name));

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

            // find collaborations for each item
            var itemsParams = new List<ItemParams>();
            foreach (var item in internallyCollabedItems)
            {
                // fetch all collaborations on this item
                var itemCollabs = item.Type == "file"
                        ? await Query(log, ()=>GetFileCollaborators(boxClient, item.Id), 
                            $"Fetch file collabs for {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", item.Name, item.Id)
                        : await Query(log, ()=>GetFolderCollaborators(boxClient, item.Id), 
                            $"Fetch folder collabs for {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", item.Name, item.Id);

                // find the collaboration associated with this user.
                var userCollabs = itemCollabs
                    .Where(c => c.AccessibleBy.Id == args.UserId)
                    .Select(c => new ItemParams(args, c.Id, c.Type, item.Name))
                    .ToList();

                itemsParams.AddRange(userCollabs);
            }

            return itemsParams;
        }

        [FunctionName(nameof(RemoveItem))]
        public static async Task RemoveItem([ActivityTrigger] IDurableActivityContext context, 
            ExecutionContext ctx)
        {
            var args =  context.GetInput<ItemParams>();  
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);
            var boxClient = await Common.GetBoxUserClient(log, args.UserId);

            if (args.ItemType == "file")
            {
                await Command(log, ()=> boxClient.FilesManager.DeleteAsync(args.ItemId), 
                    $"Remove {{{Constants.ItemType}}} {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", args.ItemType, args.ItemName, args.ItemId);
            }
            else if (args.ItemType == "folder")
            {
                await Command(log, ()=> boxClient.FoldersManager.DeleteAsync(args.ItemId, recursive: true), 
                    $"Remove {{{Constants.ItemType}}} {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", args.ItemType, args.ItemName, args.ItemId);
            }
            else if (args.ItemType == "collaboration")
            {
                await Command(log, ()=> boxClient.CollaborationsManager.RemoveCollaborationAsync(args.ItemId), 
                    $"Remove {{{Constants.ItemType}}} {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", args.ItemType, args.ItemName, args.ItemId);
                
            }
            else
            {
                log.Error($"Unrecognized item type {{{Constants.ItemType}}} for {{{Constants.ItemName}}} (item id: {{{Constants.ItemId}}})", args.ItemType, args.ItemType, args.ItemId);
            }
        }

        [FunctionName(nameof(PurgeTrashedItem))]
        public static async Task PurgeTrashedItem([ActivityTrigger] IDurableActivityContext context, 
            ExecutionContext ctx)
        {
            var args =  context.GetInput<ItemParams>();  
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);
            var boxClient = await Common.GetBoxUserClient(log, args.UserId);

            if (args.ItemType == "file")
            {
                await Command(log, ()=> boxClient.FilesManager.PurgeTrashedAsync(args.ItemId), 
                    $"Purge trashed {{{Constants.ItemType}}} {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", args.ItemType, args.ItemName, args.ItemId);
            }
            else if (args.ItemType == "folder")
            {
                await Command(log, ()=> boxClient.FoldersManager.PurgeTrashedFolderAsync(args.ItemId), 
                    $"Purge trashed {{{Constants.ItemType}}} {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", args.ItemType, args.ItemName, args.ItemId);
            }
            else
            {
                log.Error($"Unrecognized item type {{{Constants.ItemType}}} for {{{Constants.ItemName}}} (item id: {{{Constants.ItemId}}})", args.ItemType, args.ItemType, args.ItemId);
            }
        }

        [FunctionName(nameof(SetPersonalAccountQuota))]
        public static async Task SetPersonalAccountQuota([ActivityTrigger] IDurableActivityContext context, ExecutionContext ctx)
        {
            var args =  context.GetInput<MigrationParams>();
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);
           
            var boxClient = await Common.GetBoxAdminClient(log);
            var _50gb = (double)50 * 1024 * 1024 * 1024;
            var request = new BoxUserRequest() { Id = args.UserId, SpaceAmount = _50gb };
            await Command(log, () => boxClient.UsersManager.UpdateUserInformationAsync(request), 
                "Set personal account quota to 50GB");
        }
       
        [FunctionName(nameof(RollAccountOutOfEnterprise))]
        public static async Task RollAccountOutOfEnterprise([ActivityTrigger] IDurableActivityContext context, ExecutionContext ctx)
        {
            var args =  context.GetInput<MigrationParams>();
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);
           
            // roll the user out from the enterprise
            var adminToken = await Common.GetBoxAdminToken(log);
            await Command(log, () => ConvertToPersonalAccount(adminToken, args), 
                "Convert to personal account");
        }

         [FunctionName(nameof(SetAccountStatusToActive))]
        public static async Task SetAccountStatusToActive([ActivityTrigger] IDurableActivityContext context, ExecutionContext ctx)
        {
            var args =  context.GetInput<MigrationParams>();
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);

            // set user account as active
            var boxClient = await Common.GetBoxAdminClient(log);             
            var req = new BoxUserRequest() { Id = args.UserId, Status = "active" };
            await Command(log, () => boxClient.UsersManager.UpdateUserInformationAsync(req), 
                "Set account status to active");           
        }
        
        [FunctionName(nameof(ListAllTheTrashedItems))]
        public static async Task<IEnumerable<ItemParams>> ListAllTheTrashedItems([ActivityTrigger] IDurableActivityContext context, ExecutionContext ctx)
        {
            var args =  context.GetInput<MigrationParams>();
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);
           
            var boxClient = await Common.GetBoxUserClient(log, args.UserId); 
            var trashedItems = await Query(log, ()=> boxClient.FoldersManager.GetTrashItemsAsync(1000, autoPaginate: true, fields: new[] { "id", "name", "owned_by"}), 
                "List all the trashed items");            
            return trashedItems.Entries.Where(i => i.OwnedBy.Id == args.UserId).Select(i => new ItemParams(args, i.Id, i.Type, i.Name));
        }
            
        public static async Task ConvertToPersonalAccount(string adminToken, MigrationParams args)
        {            
            using (var client = new HttpClient())
            {
                var req = new HttpRequestMessage(HttpMethod.Put, $"https://api.box.com/2.0/users/{args.UserId}");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
                req.Content = new StringContent(@"{ ""notify"": false, ""enterprise"": null }", System.Text.Encoding.UTF8, "application/json");
                var resp = await client.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    var respContent = await resp.Content.ReadAsStringAsync();
                    throw new Exception($"{resp.StatusCode} '{respContent}'");
                }
            }
        }

        private static async Task<List<BoxCollaboration>> GetFolderCollaborators(BoxClient client, string itemId)
        {
            var collection = await client.FoldersManager.GetCollaborationsAsync(itemId, fields:new[]{"owned_by", "accessible_by", "item"});
            return collection.Entries;            
        }

        private static async Task<List<BoxCollaboration>> GetFileCollaborators(BoxClient client, string itemId)
        {
            var collection = await client.FilesManager.GetCollaborationsCollectionAsync(itemId, fields:new[]{"owned_by", "accessible_by", "item"}, autoPaginate: true);
            return collection.Entries;
        }

        private static async Task Command(ILogger log, Func<Task> command, string msgTemplate, params object[] msgArgs )
        {
            try
            {
                await command();
                log.Information($"{msgTemplate}", msgArgs);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"[ERR] {msgTemplate}", msgArgs);
                throw new Exception("Box command failed.", ex);
            }
        } 

        private static async Task<T> Query<T>(ILogger log, Func<Task<T>> query, string msgTemplate, params object[] msgArgs )
        {
            try
            {
                var result = await query();
                log.Information($"{msgTemplate}", msgArgs);
                return result;
            }
            catch (Exception ex)
            {
                log.Error(ex, $"[ERR] {msgTemplate}", msgArgs);
                throw new Exception("Box query failed.", ex);
            }
        } 
    }
}



