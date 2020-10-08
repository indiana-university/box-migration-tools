using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Box.V2.Models;
using System.Linq;
using Box.V2.Exceptions;

namespace box_migration_automation
{
    public static class Teardown
    {
        public class RequestParams 
        {
            // an individual person account ID
            [JsonProperty(Required = Required.Always)]
            public string UserId { get; set; }
            //UITS managed group account id that owns everything
            [JsonProperty(Required = Required.Always)]
            public string ManagedUserId { get; set; }
            //UITS managed group account folder for holding user files/folders
            [JsonProperty(Required = Required.Always)]
            public string ManagedFolderId { get; set; }
        }

        [FunctionName("Cleanup")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req, ExecutionContext ctx)
        {
            var data = await Common.DeserializeRequestBody<RequestParams>(req);
            var log = Common.GetLogger(ctx, req, data.UserId);

            try
            {
                log.Information($"Request parameters ManagedUserId={{{Constants.ManagedUserId}}} ManagedFolderId={{{Constants.ManagedFolderId}}}", data.ManagedUserId,data.ManagedFolderId);

                var adminClientTask = Common.GetBoxAdminClient(log);
                var managedClientTask = Common.GetBoxUserClient(log, data.ManagedUserId);
                await Task.WhenAll(adminClientTask, managedClientTask);
                
                var adminClient = adminClientTask.Result;
                var managedClient = managedClientTask.Result;

                log.Information($"Fetching group memberships for migrated user...");
                var groupName = Common.GroupName(data.UserId);
                var userGroups = await adminClient.UsersManager.GetMembershipsForUserAsync(data.UserId, autoPaginate: true);
                var membership = userGroups.Entries.SingleOrDefault(g => g.Group.Name == groupName);
                if (membership != null) 
                {
                    log.Information($"Removing migrated user from group {{{Constants.GroupName}}} ({{{Constants.MembershipGroupId}}})...", groupName, membership.Group.Id);
                    await adminClient.GroupsManager.DeleteAsync(membership.Group.Id);
                }
                else
                {
                    log.Information($"Migrated user is not a member of group {{{Constants.GroupName}}}.", groupName);
                } 

                log.Information($"Fetching collaborations on managed folder {{{Constants.ManagedFolderId}}}...", data.ManagedFolderId);
                var folderCollabs = await managedClient.FoldersManager.GetCollaborationsAsync(data.ManagedFolderId);
                if (folderCollabs.Entries.Any(c => c.AccessibleBy.Id == data.UserId))
                {
                    log.Information($"Migrated user is already a collaborator on folder {{{Constants.ManagedFolderId}}}.", data.ManagedFolderId);
                }
                else
                {
                    log.Information($"Creating 'viewer' collaboration on folder {{{Constants.ManagedFolderId}}} with migrated user...", data.ManagedFolderId);
                    var accessibleBy = new BoxCollaborationUserRequest() { Id = data.UserId, Type = BoxType.user };
                    var collabItem = new BoxRequestEntity() { Id = data.ManagedFolderId, Type = BoxType.folder };
                    var collabRequest = new BoxCollaborationRequest() { AccessibleBy = accessibleBy, Item = collabItem, Role = "viewer" };
                    await managedClient.CollaborationsManager.AddCollaborationAsync(collabRequest, notify: false);
                }

                return new OkObjectResult("");
            }
            catch (Exception ex)
            {
                return Common.HandleError(ex, log);
            }
        }

    }
}
