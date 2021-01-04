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
        
        static async Task  Main(string[] args)
        {
                      
            var appConfig = new ConfigurationBuilder().AddUserSecrets<Program>().Build();
            var configJson = appConfig["BoxConfigJson"];
            
            Console.WriteLine("Creating Box admin client...");
            var config = BoxConfig.CreateFromJsonString(configJson);
            var auth = new BoxJWTAuth(config);
            var adminToken = auth.AdminToken();
            var boxAdminClient = auth.AdminClient(adminToken);  

            BoxCollection<BoxUser> enterpriseUsers ;     
            uint offset = 0;      
            do
            {
                enterpriseUsers = await boxAdminClient.UsersManager.GetEnterpriseUsersAsync(limit: 1000, offset: offset);
                offset += Convert.ToUInt32(enterpriseUsers.Entries.Count());
                var filteredUsers = enterpriseUsers.Entries.Where(u=>u.Status != "cannot_delete_edit_upload");
                var userGroups = UserSubSets(filteredUsers, 10);
                foreach(var users in userGroups) {
                    var tasks = users.Select(u=> System.Console.Out.WriteLineAsync($"{DateTime.Now.ToString("ss.fff")} UserId:{u.Id}"));
                    await Task.WhenAll(tasks);
                    await Task.Delay(100);
                    
                    //await UpdateUserStatus(user.Id, boxAdminClient); 
                }
            } while (offset < enterpriseUsers.TotalCount);  
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

        private static async Task UpdateUserStatus(string boxUserId, BoxClient boxAdminClient)
        {
            await boxAdminClient.UsersManager.UpdateUserInformationAsync(new BoxUserRequest()
            {
                Id = boxUserId,
                Status = "cannot_delete_edit_upload"
            });
        }
    }
}
