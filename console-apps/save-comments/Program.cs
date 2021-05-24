using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Box.V2;
using Box.V2.Config;
using Box.V2.JWTAuth;
using Box.V2.Models;
using Microsoft.Extensions.Configuration;

namespace save_comments
{
    class Program
    {
        // Required variables
        const string BoxUserId = "";
        const string BoxFolderId = "0";
        const string CommentsFolder = "/path/to/folder";

        // Don't change anything below here
        static string LogPath = Path.Join(CommentsFolder, "log.txt");

        private static readonly string[] FolderItemFields = new[] { "id", "name", "type", "parent", "path_collection", "comment_count" };

        static async Task Main(string[] args)
        {

            var appConfig = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
            var configJson = appConfig["BoxConfigJson"];

            Console.WriteLine("Creating Box admin client...");
            var config = BoxConfig.CreateFromJsonString(configJson);
            var auth = new BoxJWTAuth(config);
            var userToken = auth.UserToken(BoxUserId);
            var boxClient = auth.UserClient(userToken, BoxUserId);

            Console.WriteLine($"Starting in folder {BoxFolderId}...");

            EnsureDirectory(CommentsFolder);
            Console.WriteLine($"Creating comment files in {CommentsFolder}...");
            await TraverseBoxFolderToFetchComments(boxClient, BoxFolderId);
        }

        private static void EnsureDirectory(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        private static async Task TraverseBoxFolderToFetchComments(BoxClient boxClient, string boxFolderId)
        {
            var items = await boxClient.FoldersManager.GetFolderItemsAsync(boxFolderId, limit: 1000, autoPaginate: true, fields: FolderItemFields);
            var folders = items.Entries.Where(e => e.Type == "folder").ToList();
            var files = items.Entries.Where(e => e.Type == "file").Cast<BoxFile>().ToList();

            foreach (var file in files.Where(f => f.CommentCount > 0))
            {
                Console.WriteLine($" 💡 {file.CommentCount} comment(s) on {file.Name}");
                await SaveComments(boxClient, file);
            }

            foreach (var subfolder in folders)
            {
                Console.WriteLine($"🔍 Scanning {PathToItem(subfolder)}/{subfolder.Name} ({subfolder.Id})...");
                await TraverseBoxFolderToFetchComments(boxClient, subfolder.Id);
            }
        }

        private static async Task SaveComments(BoxClient boxClient, Box.V2.Models.BoxItem file)
        {
            var comments = await boxClient.FilesManager.GetCommentsAsync(file.Id);
            if(comments.Entries.Count > 0)
            {
                var path = PathToItem(file);
                var boxFilePath = Path.Join(path, file.Name);
                var commentFilePath = Path.Join(CommentsFolder, $"{boxFilePath}.csv");
                var content = BuildCsv(comments);
                EnsureDirectory(Path.Join(CommentsFolder, path));
                await File.WriteAllTextAsync(commentFilePath, content);
                await File.AppendAllLinesAsync(LogPath, new[]{commentFilePath});
            }
        }

        private static string PathToItem(BoxItem item)
        {
            var filePathParts = item.PathCollection.Entries.Select(f => f.Name);
            return String.Join('/', filePathParts);
        }

        private static string BuildCsv(BoxCollection<BoxComment> comments)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Timestamp,CommenterName,CommenterUsername,Comment");
            foreach (var comment in comments.Entries.OrderBy(e => e.CreatedAt))
            {
                var timestamp = comment.CreatedAt;
                var login = comment.CreatedBy.Login;
                var commenter = comment.CreatedBy.Name;
                var message = comment.Message;
                builder.AppendLine($@"{timestamp},""{commenter}"",{login},""{message}""");
            }
            return builder.ToString();
        }
    }
}
