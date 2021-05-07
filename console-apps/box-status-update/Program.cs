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


namespace boxaccount_read_only
{
    class Program
    {
        const string LogFilePath = "/path/to/logfile.csv";
        static string[] ExclusionList = new string[]{
            // put in here the usernames of any accounts you wish to 
            // exclude from the readonly conversion. 
        };
        const int MaxConcurrentUpdates = 8;
        static string[] UserFields = new[]{"id", "name", "login", "status"};
        private const string BoxStatus = "inactive";

        static async Task Main(string[] args)
        {
            ConsoleLog("Creating Box admin client...");
            BoxClient boxAdminClient = CreateBoxAdminClient();

            ConsoleLog("Fetching enterprise users...");
            var enterpriseUsers = await boxAdminClient.UsersManager.GetEnterpriseUsersAsync(limit: 1000, fields: UserFields, autoPaginate: true);

            ConsoleLog($"Filtering {enterpriseUsers.Entries.Count()} users...");
            var filteredUsers = enterpriseUsers.Entries
                .OrderBy(u => u.Login)
                .Where(u => false == AccountIsInactive(u))
                .Where(u => false == AccountInExclusionList(u))
                .ToList();

            ConsoleLog($"Updating {filteredUsers.Count()} users...");
            var startTime = DateTime.Now;
            var processedCount = 0;
            foreach (var users in UserSubSets(filteredUsers, MaxConcurrentUpdates))
            {
                var tasks = users.Select(u => UpdateUserStatus(u, boxAdminClient)).ToList();
                await Task.WhenAll(tasks);
                processedCount += tasks.Count();
                if (processedCount % (MaxConcurrentUpdates * 10) == 0)
                {
                    // log stats every 10 rounds
                    PrintTimeStats(startTime, filteredUsers.Count(), processedCount);
                }
                await LogResults(tasks);
                await Task.Delay(100);
            }

            PrintTimeStats(startTime, filteredUsers.Count(), processedCount);
            ConsoleLog("Done!");
        }

        private static bool AccountIsInactive(BoxUser u) 
            => u.Status == "inactive";

        private static bool AccountInExclusionList(BoxUser u)
        {
            var username = u.Login.Split("@", StringSplitOptions.RemoveEmptyEntries).First();
            return ExclusionList.Any(dnt => string.Equals(dnt, username, StringComparison.InvariantCultureIgnoreCase));
        }

        private static async Task LogResults(List<Task<UpdateResult>> tasks)
        {
            var lines = tasks.Select(t => t.Result.ToString());
            await File.AppendAllLinesAsync(LogFilePath, lines);
        }

        private static void PrintTimeStats(DateTime startTime, int totalCount, int processedCount)
        {
            var timePerUserInMs = Math.Ceiling((float)(DateTime.Now - startTime).TotalMilliseconds / processedCount);
            var usersRemaining = totalCount - processedCount;
            var timeRemainingInMin = Math.Ceiling((timePerUserInMs * usersRemaining) / (1000 * 60));
            ConsoleLog($"Processed {processedCount} of {totalCount}. {timePerUserInMs} ms/user. Est {timeRemainingInMin} mins remaining.");
        }

        private static void ConsoleLog(string msg)
        {
            Console.WriteLine($"[{DateTime.Now.ToString("s")}] {msg}");
        }

        private static BoxClient CreateBoxAdminClient()
        {
            var appConfig = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
            var configJson = appConfig["BoxConfigJson"];
            if (string.IsNullOrWhiteSpace(configJson))
                throw new Exception("Missing user secret: BoxConfigJson");
            var config = BoxConfig.CreateFromJsonString(configJson);
            var auth = new BoxJWTAuth(config);
            var adminToken = auth.AdminToken();
            var boxAdminClient = auth.AdminClient(adminToken);
            return boxAdminClient;
        }

        public static IEnumerable<List<T>> UserSubSets<T>(IEnumerable<T> enterpriseUsers, int max)
        {
            List<T> toReturn = new List<T>(max);
            foreach(var item in enterpriseUsers)
            {
                    toReturn.Add(item);
                    if (toReturn.Count == max)
                    {
                            yield return toReturn;
                            toReturn = new List<T>(max);
                    }
            }
            if (toReturn.Any())
            {
                    yield return toReturn;
            }
        }    

        private class UpdateResult
        {
            public UpdateResult(BoxUser user, Exception ex = null)
            {
                User = user;
                Timestamp = DateTime.Now;
                Exception = ex;
                Success = ex == null;
            }

            public BoxUser User { get; }
            public DateTime Timestamp { get; }
            public bool Success { get; }
            public Exception Exception { get; }
            public string Status { get; }

            public override string ToString() 
                => $"{Timestamp.ToString("s")},{User.Login},{User.Name},{User.Status},{(Exception == null ? string.Empty : Exception.ToString())}";
        }
        private static Random Rnd = new Random();
        private static async Task<UpdateResult> UpdateUserStatus(BoxUser user, BoxClient boxAdminClient)
        {
            try
            {
                await Task.Delay(Rnd.Next(0,25));
                await boxAdminClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
                {
                    Id = user.Id,
                    Status = BoxStatus
                });
                return new UpdateResult(user);                
            }
            catch (System.Exception ex)
            {
                return new UpdateResult(user, ex);
            }
        }
    }
}
