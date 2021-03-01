using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Box.V2.Models.Request;
using Box.V2.Models;
using Box.V2.Exceptions;
using Box.V2;
using System.Linq;
using System.Net;
using Serilog;

namespace box_migration_automation
{
    public static class Bootstrap
    {
        public class RequestParams
        {
            // The login of the user we're making a folder and group for.
            [JsonProperty(Required = Required.Always)]
            public string UserLogin { get; set; }

            //UITS managed group account id that owns everything
            [JsonProperty(Required = Required.Always)]
            public string ManagedUserId { get; set; }
        }

        public class ResponseParams 
        {
            // The id of the user we're making a folder and group for.
            [JsonProperty(Required = Required.Always)]
            public string UserId { get; set; }

            //UITS managed group account folder for holding user files/folders
            [JsonProperty(Required = Required.Always)]
            public string ManagedFolderId { get; set; }
        }

        [FunctionName("Bootstrap")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ExecutionContext ctx)
        {
            var data = await Common.DeserializeRequestBody<RequestParams>(req);
            var log = Common.GetLogger(ctx, req, null, data.UserLogin);

            try
            {
                log.Information($"Request parameters ManagedUserId={{{Constants.ManagedUserId}}}", data.ManagedUserId);
                var adminClient = await Common.GetBoxAdminClient(log);              
                var user = await Common.GetUserForLogin(log, adminClient, data.UserLogin);
                log = Common.GetLogger(ctx, req, user.Id, data.UserLogin);

                var managedClient = await Common.GetBoxUserClient(log, data.ManagedUserId);
                var managedFolderName = GetNameForManagedFolder(log, adminClient, user);
                var managedFolderId = await CreateManagedFolder(log, managedClient, managedFolderName);
                var group = await CreateGroupForUser(log, adminClient, user.Id);
                await AddUserToGroup(log, adminClient, group.Id, user.Id);
                await CreateManagedFolderCollabWithGroup(log, managedClient, group.Id, managedFolderId);

                var response = new ResponseParams() { UserId=user.Id, ManagedFolderId=managedFolderId };
                return new OkObjectResult(response);
            }
            catch (Exception ex)
            {
                return Common.HandleError(ex, log);
            }
        }


        private static async Task CreateManagedFolderCollabWithGroup(ILogger log, BoxClient managedClient, string groupId, string managedFolderId)
        {
            log.Information($"Fetching collabs on folder {{{Constants.ManagedFolderId}}}...", managedFolderId);
            var folderCollabs = await managedClient.FoldersManager.GetCollaborationsAsync(managedFolderId);
            if (folderCollabs.Entries.Any(gc => gc.AccessibleBy.Id == groupId))
            {
                log.Information($"Group {{{Constants.GroupId}}} is already a collaborator on managed folder {{{Constants.ManagedFolderId}}}.", groupId, managedFolderId);
            }
            else
            {
                log.Information($"Creating collaboration with group {{{Constants.GroupId}}} on managed folder {{{Constants.ManagedFolderId}}}...", groupId, managedFolderId);
                var accessibleBy = new BoxCollaborationUserRequest() { Id = groupId, Type = BoxType.group };
                var collabItem = new BoxRequestEntity() { Id = managedFolderId, Type = BoxType.folder };
                var collaborationRequest = new BoxCollaborationRequest() { AccessibleBy = accessibleBy, Item = collabItem, Role = "editor" };
                var groupCollab = await managedClient.CollaborationsManager.AddCollaborationAsync(collaborationRequest, notify: false);
            }
        }

        private static async Task AddUserToGroup(ILogger log, BoxClient adminClient, string groupId, string userId)
        {
            log.Information($"Fetching membership of group {{{Constants.GroupId}}}", groupId);
            var groupMembers = await adminClient.GroupsManager.GetAllGroupMembershipsForGroupAsync(groupId);
            if (groupMembers.Entries.Any(gm => gm.User.Id == userId))
            {
                log.Information($"Migrated user is already in group {{{Constants.GroupId}}}.", groupId);
            }
            else
            {
                log.Information($"Adding migrated user to group {{{Constants.GroupId}}}...", groupId);
                var groupRequest = new BoxGroupRequest() { Id = groupId };
                var groupMember = new BoxRequestEntity() { Id = userId };
                var membershipRequest = new BoxGroupMembershipRequest() { Group = groupRequest, Role = "member", User = groupMember };
                var membership = await adminClient.GroupsManager.AddMemberToGroupAsync(membershipRequest);
            }
        }

        private static async Task<BoxGroup> CreateGroupForUser(ILogger log, BoxClient adminClient, string userId)
        {
            var groupName = Common.GroupName(userId);
            try
            {
                log.Information($"Attempting to creating group {{{Constants.GroupName}}} (it might already exist)...", groupName);
                var group = await adminClient.GroupsManager.CreateAsync(new BoxGroupRequest() { Name = groupName });
                log.Information($"Created group {{{Constants.GroupName}}} with ID {{{Constants.GroupId}}}.", groupName, group.Id);
                return group;
            }
            catch (Exception ex)
            {
                if (ex is BoxException && ((BoxException)ex).StatusCode == HttpStatusCode.Conflict)
                {
                    log.Information($"Group {{{Constants.GroupName}}} appears to already exist. Finding it in existing groups...", groupName);
                    var allgroups = await adminClient.GroupsManager.GetAllGroupsAsync(autoPaginate: true);
                    var group = allgroups.Entries.Single(e => e.Name == groupName);
                    log.Information($"Found group {{{Constants.GroupName}}} with ID {{{Constants.GroupId}}}.", groupName, group.Id);
                    return group;
                }
                else
                {
                    throw ex;
                }
            }
        }
        public static async Task<string> CreateManagedFolder(ILogger log, BoxClient managedClient, string managedFolderName)
        {
            // Try to create a managed folder.
            // If successful, return the created folder ID
            BoxItem managedFolder = null;
            try
            {
                log.Information($"Creating folder '{{{Constants.ManagedFolderName}}}'...", managedFolderName);
                BoxFolderRequest folderRequest = new BoxFolderRequest() { Name = managedFolderName, Parent = new BoxRequestEntity(){Id="0"} };
                managedFolder = await managedClient.FoldersManager.CreateAsync(folderRequest);
                log.Information($"Created managed folder '{{{Constants.ManagedFolderName}}}' with ID {{{Constants.ManagedFolderId}}}.", managedFolderName, managedFolder.Id);
                return managedFolder.Id;
            }
            catch(Exception ex)
            {
                if(ex is BoxException && ((BoxException)ex).StatusCode == HttpStatusCode.Conflict)
                {
                    log.Information($"A folder named '{{{Constants.ManagedFolderName}}}' already exists, attempting to find its Id...", managedFolderName);
                    var folder = await GetExistingFolder(log, managedClient, managedFolderName);
                    if(folder == null)
                    {
                        log.Error($"Box reports that a folder named '{{{Constants.ManagedFolderName}}}' exists in the managed account, but it could not be found...", managedFolderName);
                        throw new Exception($"Box reports that a folder named '{managedFolderName}' exists in the managed account, but it could not be found...");
                    }
                    else
                    {
                        return folder.Id;
                    }
                }
                else
                {
                    throw ex;
                }
            }
        }

        private static async Task<BoxItem> GetExistingFolder(ILogger log, BoxClient managedClient, string managedFoldername, int recordsPerRequest = 500, int offset = 0)
        {
            var folders = await managedClient.FoldersManager.GetFolderItemsAsync(id: "0", limit: recordsPerRequest, offset: offset, fields: new []{ "id", "name" }, autoPaginate: true);
            var folder = folders.Entries.SingleOrDefault(f => f.Name == managedFoldername);

            if(folder != null)
            {
                log.Information($"Found folder '{{{Constants.ManagedFolderName}}}' with Id '{{{Constants.ManagedFolderId}}}'", managedFoldername, folder.Id);
                return folder;
            }
            else if(folders.Entries.Count != 0)
            {
                return await GetExistingFolder(log, managedClient, managedFoldername, recordsPerRequest, (offset + folders.Entries.Count));
            }
            else 
            {
                log.Information($"Could not find existing folder '{{{Constants.ManagedFolderName}}}'", managedFoldername, folder.Id);
                return null;
            }
        }
        
        private static string GetNameForManagedFolder(ILogger log, BoxClient adminClient, BoxUser user)
        {
            var managedFolderName = $"{user.Name} ({user.Login}) Files and Folders";
            log.Information($"Managed folder for migrated user will be {{{Constants.ManagedFolderName}}}", managedFolderName);
            return managedFolderName;
        }
    }
}
