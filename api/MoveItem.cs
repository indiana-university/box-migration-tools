using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Linq;
using Box.V2.Models;
using Box.V2;

namespace box_migration_automation
{
    public static class MoveItem
    {
        public class RequestParams 
        {
            // an individual person account ID
            [JsonProperty(Required = Required.Always)]
            public string UserId { get; set; }
            // ID of the folder or item
            [JsonProperty(Required = Required.Always)]
            public string ItemId { get; set; }
            // Is it a folder or a file
            [JsonProperty(Required = Required.Always)]
            public string ItemType { get; set; }
            //UITS managed group account folder for holding user files/folders
            [JsonProperty(Required = Required.Always)]
            public string ManagedFolderId { get; set; }
        }

        [FunctionName("MoveItem")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,ExecutionContext ctx)
        {
            var data = await Common.DeserializeRequestBody<RequestParams>(req);
            var log = Common.GetLogger(ctx, req, data.UserId, null);

            try
            {
                log.Information($"Request parameters ItemId={{{Constants.ItemId}}} ManagedFolderId={{{Constants.ManagedFolderId}}}", data.ItemId, data.ManagedFolderId);

                var userClient = await Common.GetBoxUserClient(log, data.UserId);
                var item = await GetItem(data, log, userClient);
                if (item.Parent.Id == data.ManagedFolderId)
                {
                    log.Information($"Nothing to do: {{{Constants.ItemType}}} {{{Constants.ItemId}}} is already in managed folder {{{Constants.ManagedFolderId}}}.", data.ItemType, data.ItemId, data.ManagedFolderId);
                }
                else
                {
                    await DoMoveItem(data, log, userClient);
                }

                return new OkObjectResult("");
            }
            catch (Exception ex)
            {
                return Common.HandleError(ex, log);
            }  
        }

        private static async Task<BoxItem> GetItem(RequestParams data, Serilog.ILogger log, BoxClient userClient)
        {
            log.Information($"Fetching {{{Constants.ItemType}}} {{{Constants.ItemId}}}...", data.ItemType, data.ItemId, data.ManagedFolderId);
            return (data.ItemType == "file")
                ? (BoxItem) await userClient.FilesManager.GetInformationAsync(data.ItemId, fields:new[]{"parent"})
                : (BoxItem) await userClient.FoldersManager.GetInformationAsync(data.ItemId, fields:new[]{"parent"});
        }

        private static async Task DoMoveItem(RequestParams data, Serilog.ILogger log, BoxClient userClient)
        {
            log.Information($"Moving {{{Constants.ItemType}}} {{{Constants.ItemId}}} to managed folder {{{Constants.ManagedFolderId}}}...", data.ItemType, data.ItemId, data.ManagedFolderId);
            var destination = new BoxRequestEntity() { Id = data.ManagedFolderId };
            if (data.ItemType == "file")
            {
                var fileRequest = new BoxFileRequest() { Id = data.ItemId, Parent = destination };
                await userClient.FilesManager.UpdateInformationAsync(fileRequest);
            }
            else
            {
                var folderRequest = new BoxFolderRequest() { Id = data.ItemId, Parent = destination };
                await userClient.FoldersManager.UpdateInformationAsync(folderRequest);
            }
        }
    }
}
