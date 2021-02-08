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


namespace box_migration_automation
{
    public static class AccountMigration    
    {
        public class RequestParams
        { 
            // A Box Account login/email address
            public string UserEmail { get; set; }                 

            public virtual string MsgTag => $"[{UserEmail}]";
        }

        public class MigrationParams : RequestParams
        {
            // a Box Account ID
            public string UserId { get; set; } 

            public override string MsgTag => $"[{UserEmail} ({UserId})]";
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
        public static async Task<HttpResponseMessage> MigrateToPersonalAccount(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
            [DurableClient] IDurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.    
            var args = await req.Content.ReadAsAsync<RequestParams>();

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
            var retryOptions = new RetryOptions(
                    firstRetryInterval: TimeSpan.FromSeconds(5),
                    maxNumberOfAttempts: 3){
                        Handle = ex => throw LogError(log, ex, "")
                    } ;
    
            IEnumerable<ItemParams> itemsToProcess;
            int rounds = 0;
            do
            {
                // Generate list of collaborations to remove
                itemsToProcess = await context.CallActivityWithRetryAsync<IEnumerable<ItemParams>>(
                    nameof(GetBoxItemsToProcess), retryOptions, args);

                var itemTasks = itemsToProcess.Select(itemParams => 
                    context.CallActivityWithRetryAsync(nameof(ProcessItem), retryOptions, itemParams));

                // Fan-in to await removal of collaborations
                await Task.WhenAll(itemTasks);
            } while (itemsToProcess.Count() != 0 && (rounds++) < 100);
            
            // log.LogInformation($"{args.MsgTag} Finished processing items after {rounds} rounds.");
           
            await context.CallActivityWithRetryAsync(nameof(RollAccountOutOfEnterprise), retryOptions, args);
            await context.CallActivityWithRetryAsync(nameof(SendUserNotification), retryOptions, args);
        }  

        public static async Task<(bool success, string msg)> UserAccountId(ILogger log, RequestParams args)
        {
            var boxClient = CreateBoxAdminClient(log);
            try
            {
                log.LogDebug($"{args.MsgTag} Fetching account for login...");
                var users = await boxClient.UsersManager.GetEnterpriseUsersAsync(filterTerm: args.UserEmail);
                if(users.Entries.Count > 1)
                {
                    log.LogWarning($"{args.MsgTag} Multiple Box accounts found for login.");
                    return (false, $"More than one Box account found for {args.UserEmail}");
                }
                else if(users.Entries.Count == 0)
                {
                    log.LogWarning($"{args.MsgTag} No Box account found for login.");
                    return (false, $"No Box account was found for {args.UserEmail}");
                }
                else
                {
                    var userId = users.Entries.Single().Id;
                    log.LogInformation($"[{args.UserEmail} ({userId})] Found single account for login.");
                    return (true, userId);
                }
            }
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} Failed to fetch account for login...");
            }
        }


        [FunctionName(nameof(GetBoxItemsToProcess))]
        public static async Task<IEnumerable<ItemParams>> GetBoxItemsToProcess([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args = context.GetInput<MigrationParams>();

            // get a box client for args.UserId
            var boxClient = CreateBoxUserClient(log, args.UserId);

            // list items in account root
            var items = await boxClient.FoldersManager
                .GetFolderItemsAsync(id: "0", limit: 1000, offset: 0, fields: new[] { "id", "name", "owned_by", "is_externally_owned", "path_collection"}, autoPaginate: true);

            var ownedItems = ResolveOwnedItems(log, args, boxClient, items);
            var internalCollabs = await ResolveInternalCollaborations(log, args, boxClient, items);           
            return ownedItems.Concat(internalCollabs).OrderBy(i => i.ItemName);
        }

        private static IEnumerable<ItemParams> ResolveOwnedItems(ILogger log, MigrationParams args, BoxClient boxClient, BoxCollection<BoxItem> items)
        {
            return 
                items.Entries
                .Where(i => i.OwnedBy.Id == args.UserId)
                .Select(i => new ItemParams(args, i.Id, i.Type, i.Name));

        }

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
                log.LogDebug($"{args.MsgTag} Fetching collabs for {item.Type} {item.Name} (id: {item.Id}");
                var itemCollabs = item.Type == "file"
                        ? await GetFileCollaborators(log, args, boxClient, item.Id, item.Name)
                        : await GetFolderCollaborators(log, args, boxClient, item.Id, item.Name);

                // find the collaboration associated with this user.
                var userCollabs = itemCollabs
                    .Where(c => c.AccessibleBy.Id == args.UserId)
                    .Select(c => new ItemParams(args, c.Id, c.Type, item.Name))
                    .ToList();

                itemsParams.AddRange(userCollabs);
                // log.LogInformation($" Collabs on {item.Id} {item.Name}: {userCollabs.Count}");
            }

            return itemsParams;
        }

        [FunctionName(nameof(ProcessItem))]
        public static async Task ProcessItem([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<ItemParams>();  
            var boxClient = CreateBoxUserClient(log, args.UserId);

            if (args.ItemType == "file")
            {
                await TryRemoveFile(log, args, boxClient);
            }
            else if (args.ItemType == "folder")
            {
                await TryRemoveFolder(log, args, boxClient);

            }
            else if (args.ItemType == "collaboration")
            {
                await TryRemoveCollaboration(log, args, boxClient);
            }
            else
            {
                log.LogError($"{args.MsgTag} Unrecognized item type {args.ItemType} for {args.ItemName} (item id: {args.ItemId})");
            }
        }

        private static async Task TryRemoveFile(ILogger log, ItemParams args, BoxClient boxClient)
        {
            try
            {
                log.LogDebug($"{args.MsgTag} Removing file {args.ItemName} ({args.ItemId})...");
                await boxClient.FilesManager.DeleteAsync(args.ItemId);
                await boxClient.FilesManager.PurgeTrashedAsync(args.ItemId);
                log.LogInformation($"{args.MsgTag} Removed file {args.ItemName} ({args.ItemId}).");
            }
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} Failed to remove file {args.ItemName} ({args.ItemId}).");             
            } 
        }

        public static async Task TryRemoveFolder(ILogger log, ItemParams args, BoxClient boxClient)
        {
            try
            {
                log.LogDebug($"{args.MsgTag} Removing folder {args.ItemName} ({args.ItemId})...");
                await boxClient.FoldersManager.DeleteAsync(args.ItemId, recursive: true);
                await boxClient.FoldersManager.PurgeTrashedFolderAsync(args.ItemId);
                log.LogInformation($"{args.MsgTag} Removed folder {args.ItemName} ({args.ItemId}).");
            }
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} Failed to remove folder {args.ItemName} ({args.ItemId}).");
            }                
        }

        public static async Task TryRemoveCollaboration(ILogger log, ItemParams args, BoxClient boxClient)
        {
            try
            {
                log.LogDebug($"{args.MsgTag} Removing collab on {args.ItemName} ({args.ItemId})...");
                await boxClient.CollaborationsManager.RemoveCollaborationAsync(args.ItemId);
                log.LogInformation($"{args.MsgTag} Finished removing collab on {args.ItemName} ({args.ItemId}).");
            }
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} Failed to remove collab on {args.ItemName} ({args.ItemId}).");
            }                
        }

        [FunctionName(nameof(RollAccountOutOfEnterprise))]
        public static async Task RollAccountOutOfEnterprise([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            var args =  context.GetInput<MigrationParams>();
            // get a box admin client
            var boxClient = CreateBoxAdminClient(log);             
            // set user account as active
            await TrySetAccountStatusToActive(log, args, boxClient);

            var adminToken = CreateBoxAdminToken(log);
            // roll the user out from the enterprise
            await TryConvertToPersonalAccount(log, args, adminToken);
        }

        public static async Task TrySetAccountStatusToActive(ILogger log, MigrationParams args, BoxClient boxClient)
        {
            try
            {
                log.LogDebug($"{args.MsgTag} Setting account status to 'active'...");
                await boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest() { Id = args.UserId, Status = "active" });  
                log.LogInformation($"{args.MsgTag} Set account status to 'active'.");
            } 
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} Failed to set account status to 'active'.");
            }
        }

        public static async Task TryConvertToPersonalAccount(ILogger log, MigrationParams args, string adminToken)
        {            
            try
            {
                log.LogDebug($"{args.MsgTag} Converting to personal account...");
                using (var client = new HttpClient())
                {
                    var req = new HttpRequestMessage(HttpMethod.Put, $"https://api.box.com/2.0/users/{args.UserId}");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
                    req.Content = new StringContent(@"{ ""notify"": true, ""enterprise"": null }", System.Text.Encoding.UTF8, "application/json");
                    var resp = await client.SendAsync(req);
                    if (!resp.IsSuccessStatusCode)
                    {
                        var respContent = await resp.Content.ReadAsStringAsync();
                        throw new Exception($"{resp.StatusCode} '{respContent}'");
                    }
                }
                log.LogInformation($"{args.MsgTag} Converted to personal account.");
            } 
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} Failed to convert to personal account.");
            }
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

            try
            {
                log.LogDebug($"{args.MsgTag} Sending account rollout notification...");
                //await client.SendEmailAsync(message);
                log.LogInformation($"{args.MsgTag} Sent account rollout notification.");
            }
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} Failed to send account rollout notification.");
            }

        }

        public static BoxClient CreateBoxUserClient(ILogger log, string userId)
        {
            try
            {
                var auth = CreateBoxJwtAuth();
                var userToken = auth.UserToken(userId);
                return auth.UserClient(userToken, userId);  
            }
            catch (Exception ex)
            {
                throw LogError(log, ex, "Failed to create Box admin client.");                
            }
        }

        public static BoxClient CreateBoxAdminClient(ILogger log)
        {
            try
            {
                var auth = CreateBoxJwtAuth();
                var adminToken = auth.AdminToken();
                return auth.AdminClient(adminToken);  
            }
            catch (Exception ex)
            {
               throw  LogError(log, ex, "Failed to create Box admin client.");
            }
        }


        public static string CreateBoxAdminToken(ILogger log)
        {
            try
            {
                var auth = CreateBoxJwtAuth();
                var adminToken = auth.AdminToken();
                return adminToken;
            }
            catch (Exception ex)
            {
               throw  LogError(log, ex, "Failed to create Box admin token.");
            }
        }        
        
        public static BoxJWTAuth CreateBoxJwtAuth()
        {
            var boxConfigJson = System.Environment.GetEnvironmentVariable("BoxConfigJson");
            var config = BoxConfig.CreateFromJsonString(boxConfigJson);            
            return new BoxJWTAuth(config);
        }

        private static async Task<List<BoxCollaboration>> GetFolderCollaborators(ILogger log, MigrationParams args, BoxClient client, string itemId, string itemName)
        {
            try
            {
                var collection = await client.FoldersManager.GetCollaborationsAsync(itemId, fields:new[]{"owned_by", "accessible_by", "item"});
                return collection.Entries;
            }   
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} Failed to fetch folder collabs for {itemName} ({itemId})");
            }
        }

        private static async Task<List<BoxCollaboration>> GetFileCollaborators(ILogger log, MigrationParams args, BoxClient client, string itemId, string itemName)
        {
            try 
            {
                var collection = await client.FilesManager.GetCollaborationsCollectionAsync(itemId, fields:new[]{"owned_by", "accessible_by", "item"}, autoPaginate: true);
                return collection.Entries;
            }
            catch(Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} Failed to fetch file collabs for {itemName}({itemId})");
            }
        }

        private static async Task Command(ILogger log, MigrationParams args, string msg, Func<Task> command)
        {
            try
            {
                log.LogDebug($"{args.MsgTag} [TRY] {msg}");
                await command();
                log.LogInformation($"{args.MsgTag} [OK] {msg}");
            }
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} [ERR] {msg}");
            }
        } 

        private static async Task<T> Query<T>(ILogger log, MigrationParams args, string msg, Func<Task<T>> query)
        {
            try
            {
                log.LogDebug($"{args.MsgTag} [TRY] {msg}");
                var result =  await query();
                log.LogInformation($"{args.MsgTag} [OK] {msg}");
                return result;
            }
            catch (Exception ex)
            {
                throw LogError(log, ex, $"{args.MsgTag} [ERR] {msg}");
            }
        } 

        private static Exception LogError(ILogger log, Exception ex, string msg)
        {
            log.LogError(ex, msg);
            return new Exception(msg, ex);
        }
    }
}



