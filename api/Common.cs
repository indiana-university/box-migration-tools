using System;
using System.Threading.Tasks;
using Box.V2.Config;
using Box.V2;
using Box.V2.JWTAuth;
using Box.V2.Models;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Box.V2.Exceptions;
using Microsoft.AspNetCore.Http;
using Serilog;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.WindowsAzure.Storage;
using Microsoft.Azure.WebJobs;
using System.IO;
using Newtonsoft.Json;

namespace box_migration_automation
{
    public class KeyGenerator : Serilog.Sinks.AzureTableStorage.KeyGenerator.DefaultKeyGenerator
    {
        public override string GeneratePartitionKey(Serilog.Events.LogEvent logEvent)
        {
            // return the migrated UserId, if available.
            if (logEvent.Properties.TryGetValue(Constants.UserId, out var userId))
            {
                using (var stringWriter = new StringWriter())
                {
                    userId.Render(stringWriter);
                    return stringWriter.ToString().Trim(' ', '"');
                }
            }
            else
            {
                return base.GeneratePartitionKey(logEvent);
            }
        }

        public override string GenerateRowKey(Serilog.Events.LogEvent logEvent, string suffix = null)
        {
            // return the timestamp in ticks
            var utcEventTime = logEvent.Timestamp.UtcDateTime;
            var timeWithoutMilliseconds = utcEventTime.AddMilliseconds(-utcEventTime.Millisecond);
            var suffixFormatted = string.IsNullOrWhiteSpace(suffix) ? String.Empty : $"|{suffix}";
            return $"0{timeWithoutMilliseconds.Ticks}{suffixFormatted}";
        }
    }

    public static class Common
    {
        public static string Env(string key, bool required=false)
        {
            var value = System.Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(value) && required)
            {
                throw new Exception($"'{key}' is missing from the environment. This is a required setting.");
            }
            return value;
        }

        private static string[] PropertyColumns = new string[]{
            Constants.FunctionName,
            Constants.InvocationId,
            Constants.CorrelationId,
            Constants.IPAddress,
            Constants.AccessibleById,
            Constants.CollaborationId,
            Constants.GroupId,
            Constants.GroupName,
            Constants.ItemId,
            Constants.ItemName,
            Constants.ItemType,
            Constants.ManagedFolderId,
            Constants.ManagedFolderName,
            Constants.ManagedUserId,
            Constants.MembershipGroupId ,
            Constants.UserId,
            Constants.UserName ,
            Constants.UserLogin,
            Constants.BoxClientId,
            Constants.StatusCode,
            Constants.ErrorInfo,
            Constants.ErrorStackTrace
        };

        private static ILogger CreateLogger()
        {
            var loggerConfiguration = 
                new LoggerConfiguration()
                    .Enrich.FromLogContext()
                    .WriteTo.Console();

            // Log to Azure Table Storage if connection string is available.
            var tableStorageConnectionString = Env("LogTableStorageConnectionString");
            if (!string.IsNullOrWhiteSpace(tableStorageConnectionString))
            {
                loggerConfiguration
                    .WriteTo.AzureTableStorageWithProperties(
                        CloudStorageAccount.Parse(tableStorageConnectionString)
                        , storageTableName: "BoxMigrationLogs"
                        , propertyColumns: PropertyColumns
                        , keyGenerator: new KeyGenerator());
            }

            // Log to Azure App Insights if key is available.
            var appInsightsKey = Env("APPINSIGHTS_INSTRUMENTATIONKEY");
            if (!string.IsNullOrWhiteSpace(appInsightsKey))
            {
                loggerConfiguration
                    .WriteTo.ApplicationInsights(TelemetryConfiguration.CreateDefault(), TelemetryConverter.Traces);
            }

            // Log to local file path if available
            var logFilePath = Env("LogFilePath");
            if (!string.IsNullOrWhiteSpace(logFilePath))
            {
                loggerConfiguration
                    .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day);            
            }

            return loggerConfiguration.CreateLogger();
        }

        private static ILogger Logger = CreateLogger();

        public static ILogger GetLogger(ExecutionContext ctx, HttpRequest req, string userId, string userLogin)
        {
            var correlationId = 
                req.Headers.TryGetValue("CorrelationID", out var correlationIds)
                ? correlationIds.FirstOrDefault() ?? ""
                : "";

            return GetLogger(ctx, userId, userLogin)
                .ForContext(Constants.CorrelationId, correlationId)
                .ForContext(Constants.IPAddress, req.HttpContext.Connection.RemoteIpAddress);
        }

        public static ILogger GetLogger(ExecutionContext ctx, string userId, string userLogin)
        {
            return Logger
                .ForContext(Constants.FunctionName, ctx.FunctionName)
                .ForContext(Constants.InvocationId, ctx.InvocationId)
                .ForContext(Constants.UserId, userId)
                .ForContext(Constants.UserLogin, userLogin);
        }

        private static BoxJWTAuth GetJwtAuth()
        {
            var configJson = Env("BoxConfigJson", required:true);
            var config = BoxConfig.CreateFromJsonString(configJson);
            return new BoxJWTAuth(config);
        }

        public static async Task<BoxClient> GetBoxUserClient(ILogger log, string userId)
        {
            try
            {
                BoxJWTAuth boxJwtAuth = GetJwtAuth();
                var userToken = boxJwtAuth.UserToken(userId);
                var boxUserClient = boxJwtAuth.UserClient(userToken, userId);
                log.Information($"Created Box user client as {{{Constants.BoxClientId}}}...", userId);
                return await Task.FromResult(boxUserClient);
            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to create Box user client as {{{Constants.BoxClientId}}}...", userId);
                throw new Exception ($"Failed to create Box user client as {userId}", ex);
            }
        }

        public static async Task<BoxClient> GetBoxAdminClient(ILogger log)
        {
            try
            {
                BoxJWTAuth boxJwtAuth = GetJwtAuth();
                var adminToken = boxJwtAuth.AdminToken();
                var boxAdminClient = boxJwtAuth.AdminClient(adminToken);
                log.Information($"Created Box admin client." );
                return await Task.FromResult(boxAdminClient);

            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to create Box admin client." ); 
                throw new Exception ($"Failed to create Box Admin client", ex);
            }               
            
        }

        public static async Task<string> GetBoxAdminToken(ILogger log)
        {
            try
            {
                BoxJWTAuth boxJwtAuth = GetJwtAuth();
                var adminToken = boxJwtAuth.AdminToken();
                log.Information($"Created Box admin token.");
                return await Task.FromResult(adminToken);

            }
            catch (Exception ex)
            {
                log.Error(ex, $"Failed to create Box admin token." ); 
                throw new Exception ($"Failed to create Box admin token.", ex);
            }
        }

        public static IActionResult HandleError(Exception ex,ILogger log)
        {
            var logWithStackTrace = log.ForContext(Constants.ErrorStackTrace, ex.StackTrace);
            if (ex is BoxException)
            {
                var boxEx = (BoxException)ex;
                logWithStackTrace.Error($"Caught Box Exception. Will return HTTP {{{Constants.StatusCode}}}. Details: {{{Constants.ErrorInfo}}}", boxEx.StatusCode, JsonConvert.SerializeObject(boxEx.Error));
                return new JsonResult(boxEx.Error) { StatusCode = (int)boxEx.StatusCode };
            }
            else
            {
                logWithStackTrace.Error($"Caught generic exception. Will return HTTP {{{Constants.StatusCode}}}. Details: {{{Constants.ErrorInfo}}}", 500, ex.Message);
                return new JsonResult(ex) { StatusCode = 500 };
            }
        }

        public static async Task<T> DeserializeRequestBody<T>(HttpRequest req)
        {
            string requestBody = await new System.IO.StreamReader(req.Body).ReadToEndAsync();
            var data = Newtonsoft.Json.JsonConvert.DeserializeObject<T>(requestBody);
            return data;
        }

        public static string GroupName(string userId) => $"Box_Migration_Automation_{userId}";

        public static async Task<BoxUser> GetUserForLogin(ILogger log, BoxClient adminClient, string userLogin)
        {
            log.Information($"Fetching user record for login {{{Constants.UserLogin}}}...", userLogin);
            var results = await adminClient.UsersManager.GetEnterpriseUsersAsync(filterTerm: userLogin, fields: new[]{"name", "login"});
            if(results.Entries.Count > 1)
            {
                throw new Exception($"GetIdForLogin - found more than one user based on a search for '{userLogin}'");
            } 
            else if(results.Entries.Count == 0)
            {
                throw new Exception($"GetIdForLogin - No user found based on search for '{userLogin}");
            } 
            else 
            {
                var user = results.Entries.First();
                log.Information($"Found user with ID {{{Constants.UserId}}} and name {{{Constants.UserName}}} for login {{{Constants.UserLogin}}}", user.Id, user.Name, userLogin);
                return user;
            }
        }

    }
}