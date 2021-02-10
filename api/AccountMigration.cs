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
using SendGrid.Helpers.Mail;
using SendGrid;
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
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequest req,
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
            var args =  await Task.FromResult(context.GetInput<MigrationParams>()); 
            var retryOptions = new RetryOptions(
                    firstRetryInterval: TimeSpan.FromSeconds(5),
                    maxNumberOfAttempts: 3);
    
            await context.CallActivityWithRetryAsync(nameof(SetAccountStatusToActive), retryOptions, args);

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
           
            // await context.CallActivityWithRetryAsync(nameof(RollAccountOutOfEnterprise), retryOptions, args);
            await context.CallActivityWithRetryAsync(nameof(SendUserNotification), retryOptions, args);
        }  

        public static async Task<(bool success, string msg)> UserAccountId(ILogger log, RequestParams args)
        {
            var boxClient = await Common.GetBoxAdminClient(log);
            var users = await Query(log, () => boxClient.UsersManager.GetEnterpriseUsersAsync(filterTerm: args.UserEmail),
                $"Fetch Box account(s) for login.");
                
            if(users.Entries.Count > 1)
            {
                log.Warning($"Multiple Box accounts found for login.");
                return (false, $"Multiple Box accounts found for login.");
            }
            else if(users.Entries.Count == 0)
            {
                log.Warning($"No Box account found for login.");
                return (false, $"No Box account was found for login.");
            }
            else
            {
                var userId = users.Entries.Single().Id;
                log.Information($"Found single Box account {{{Constants.UserId}}} for login.", userId);
                return (true, userId);
            }
        }


        [FunctionName(nameof(GetBoxItemsToProcess))]
        public static async Task<IEnumerable<ItemParams>> GetBoxItemsToProcess([ActivityTrigger] IDurableActivityContext context, ExecutionContext ctx)
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

        [FunctionName(nameof(ProcessItem))]
        public static async Task ProcessItem([ActivityTrigger] IDurableActivityContext context, 
            ExecutionContext ctx)
        {
            var args =  context.GetInput<ItemParams>();  
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);
            var boxClient = await Common.GetBoxUserClient(log, args.UserId);

            if (args.ItemType == "file")
            {
                await Command(log, ()=>RemoveFile(boxClient, args), 
                    $"Remove {{{Constants.ItemType}}} {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", args.ItemType, args.ItemName, args.ItemId);
            }
            else if (args.ItemType == "folder")
            {
                await Command(log, ()=>RemoveFolder(boxClient, args), 
                    $"Remove {{{Constants.ItemType}}} {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", args.ItemType, args.ItemName, args.ItemId);
            }
            else if (args.ItemType == "collaboration")
            {
                await Command(log, ()=>RemoveCollaboration(boxClient, args), 
                    $"Remove {{{Constants.ItemType}}} {{{Constants.ItemName}}} ({{{Constants.ItemId}}})", args.ItemType, args.ItemName, args.ItemId);
                
            }
            else
            {
                log.Error($"Unrecognized item type {{{Constants.ItemType}}} for {{{Constants.ItemName}}} (item id: {{{Constants.ItemId}}})", args.ItemType, args.ItemType, args.ItemId);
            }
        }

        private static async Task RemoveFile(BoxClient boxClient, ItemParams args)
        {
            await boxClient.FilesManager.DeleteAsync(args.ItemId);
            await boxClient.FilesManager.PurgeTrashedAsync(args.ItemId);
        }

        public static async Task RemoveFolder(BoxClient boxClient, ItemParams args)
        {
            await boxClient.FoldersManager.DeleteAsync(args.ItemId, recursive: true);
            await boxClient.FoldersManager.PurgeTrashedFolderAsync(args.ItemId);                           
        }

        public static Task RemoveCollaboration(BoxClient boxClient, ItemParams args)
            => boxClient.CollaborationsManager.RemoveCollaborationAsync(args.ItemId);                     

        [FunctionName(nameof(RollAccountOutOfEnterprise))]
        public static async Task RollAccountOutOfEnterprise([ActivityTrigger] IDurableActivityContext context, ExecutionContext ctx)
        {
            var args =  context.GetInput<MigrationParams>();
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);
           
            // roll the user out from the enterprise
            var adminToken = await Common.GetBoxAdminToken(log);
            await Command(log, () => ConvertToPersonalAccount(adminToken, args), "Convert to personal account");
        }

         [FunctionName(nameof(SetAccountStatusToActive))]
        public static async Task SetAccountStatusToActive([ActivityTrigger] IDurableActivityContext context, ExecutionContext ctx)
        {
            var args =  context.GetInput<MigrationParams>();
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);

            // set user account as active
            var boxClient = await Common.GetBoxAdminClient(log);             
            await Command(log, () => DoSetAccountStatusToActive(boxClient, args), "Set account status to active");
           
        }

        public static Task DoSetAccountStatusToActive(BoxClient boxClient, MigrationParams args)
            => boxClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest() { Id = args.UserId, Status = "active" });  

        public static async Task ConvertToPersonalAccount(string adminToken, MigrationParams args)
        {            
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
        }
        
        [FunctionName(nameof(SendUserNotification))]
        public static Task SendUserNotification([ActivityTrigger] IDurableActivityContext context, ExecutionContext ctx)
        {
            var args =  context.GetInput<MigrationParams>();
            var log = Common.GetLogger(ctx, args.UserId, args.UserEmail);

            Task DoSend(){
                var apiKey = Environment.GetEnvironmentVariable("SendGridApiKey");
                var fromAddress = Environment.GetEnvironmentVariable("MigrationNotificationFromAddress");
                var client = new SendGridClient(apiKey);

                var message = new SendGridMessage();
                message.SetFrom(new EmailAddress(fromAddress, "Box Migration Notifications"));
                message.AddTo(new EmailAddress(args.UserEmail));
                message.SetSubject("Box account migration update");
                message.AddContent(MimeType.Text, $@"Hello, Your Box account has been migrated to a personal account. 
                Any external collaborations you may have had were preserved, but any university-related data has been removed from your account.");

                return client.SendEmailAsync(message);
            }

            return Command(log, DoSend, "Send account rollout notification");
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



