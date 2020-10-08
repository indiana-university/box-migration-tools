using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using System.Linq;
using Box.V2;

namespace box_migration_automation
{
    public static class ListSubfolders
    {
        public class RequestParams 
        {
            // an individual person account ID
            [JsonProperty(Required = Required.Always)]
            public string UserLogin { get; set; }

            // ID of the folder for which to list subfolders
            [JsonProperty(Required = Required.Always)]
            public string FolderId { get; set; }
        }

        public class Folder
        {
            // The id of the subfolder
            [JsonProperty(Required = Required.Always)]
            public string Id { get; set; }

            // The name of the subfolder
            [JsonProperty(Required = Required.Always)]
            public string Name { get; set; }
        }

        public class ResponseParams 
        {
            // A collection of subfolder records
            [JsonProperty(Required = Required.Always)]
            public Folder[] Folders { get; set; }
        }

        [FunctionName("ListSubfolders")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,ExecutionContext ctx)
        {
            var data = await Common.DeserializeRequestBody<RequestParams>(req);
            var log = Common.GetLogger(ctx, req, data.UserLogin);

            try
            {
                log.Information($"Request parameters ItemId={{{Constants.ItemId}}}", data.FolderId);

                var adminClient = await Common.GetBoxAdminClient(log);
                var user = await Common.GetUserForLogin(log, adminClient, data.UserLogin);
                log = Common.GetLogger(ctx, req, user.Id);
                var userClient = await Common.GetBoxUserClient(log, user.Id);
                var subfolders = await GetSubfolders(userClient, data.FolderId, user.Id);
                return new OkObjectResult(new ResponseParams() { Folders = subfolders });
            }
            catch (Exception ex)
            {
                return Common.HandleError(ex, log);
            }  
        }

        private static async Task<Folder[]> GetSubfolders(BoxClient userClient, string folderId, string userId)
        {
            var items = await userClient.FoldersManager.GetFolderItemsAsync(id: folderId, limit: 1000, offset: 0, fields: new[] { "id", "name", "owned_by" }, autoPaginate: true);
            return items.Entries
                .Where(i => i.Type == "folder")
                .Where(i => i.OwnedBy?.Id == userId)
                .Select(f => new Folder { Id = f.Id, Name = f.Name })
                .OrderBy(f => f.Name)
                .ToArray();
        }
    }
}
