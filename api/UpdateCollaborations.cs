using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Box.V2;
using System.Linq;
using Box.V2.Models;
using System.Collections.Generic;
using Box.V2.Exceptions;
using Serilog;

namespace box_migration_automation
{
    public static class ChangeToViewer
    {
        public class RequestParams 
        {
            // The user we're making a folder and group for.
            [JsonProperty(Required = Required.Always)]
            public string UserId { get; set; }
            // the item we want to move
            [JsonProperty(Required = Required.Always)]
            public string ItemId { get; set; }
            // whether the item is a file or folder
            [JsonProperty(Required = Required.Always)]
            public string ItemType { get; set; }
            //UITS managed group account id that owns everything
            [JsonProperty(Required = Required.Always)]
            public string ManagedUserId{ get; set;}
        }

        [FunctionName("UpdateCollaborations")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ExecutionContext ctx)
        {
            var data = await Common.DeserializeRequestBody<RequestParams>(req);
            var log = Common.GetLogger(ctx, req, data.UserId, null);

            try
            {
                log.Information($"Request parameters ItemId={{{Constants.ItemId}}} ItemType={{{Constants.ItemType}}} ManagedUserId={{{Constants.ManagedUserId}}}", data.ItemId, data.ItemType, data.ManagedUserId);

                var client = await Common.GetBoxUserClient(log, data.UserId);

                var existingCollabs = data.ItemType == "file"
                    ? await GetFileCollaborators(client, data.ItemId)
                    : await GetFolderCollaborators(client, data.ItemId);

                var collabUpdateTasks = existingCollabs
                    .Where(c => c.Role != "viewer") // don't need to modify viewers
                    .Where(c => c.Item != null && c.Item.Id == data.ItemId) // only modify collabs directly on this item
                    .Where(c => c.AccessibleBy != null && c.AccessibleBy.Id != data.UserId) // don't modify collab for the owner (extra safety)
                    .Select(c => ChangeCollaborationRoleToViewer(log, client, data.ItemId, data.ItemType, c))
                    .ToList();

                await Task.WhenAll(collabUpdateTasks);

                return new OkObjectResult("");
            }
            catch (Exception ex)
            {
                return Common.HandleError(ex, log);
            }
        }
        
        private static Task<BoxCollaboration> ChangeCollaborationRoleToViewer(ILogger log, BoxClient sourceClient, string itemId, string itemType, BoxCollaboration c)
        {
            log.Information($"Changing collaboration {{{Constants.CollaborationId}}} on {{{Constants.ItemType}}} {{{Constants.ItemId}}} to 'viewer' role for user {{{Constants.AccessibleById}}}...", c.Id, itemType, itemId, c.AccessibleBy.Id);
            var collabUpdate = new BoxCollaborationRequest() { Id = c.Id, Role = "viewer" };
            return sourceClient.CollaborationsManager.EditCollaborationAsync(collabUpdate);
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
    }
}
