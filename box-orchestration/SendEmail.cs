using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Extensions.Logging;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace boxaccountorchestration

{
    public static class SendEmail
    {
        

        [FunctionName(nameof(SendEmail))]
        public static async Task EmailNotification([ActivityTrigger] IDurableActivityContext context, ILogger log)
        {
            log.LogInformation($"Start 'SendEmail' Function....");

            var data = context.GetInput<RequestParams>();
            try
            {
                var apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY");
                var client = new SendGridClient(apiKey);
                var msg = new SendGridMessage();

                msg.SetFrom(new EmailAddress("dx@example.com", "SendGrid DX Team"));
                msg.AddTo(new EmailAddress(data.UserId));

                msg.SetSubject("Testing the SendGrid C# Library");

                msg.AddContent(MimeType.Text, $"Hello {data.UserId},  Your box account has been reactivated!");
                //msg.AddContent(MimeType.Html, $"<p> Hello {data.UserId}, Your box account has been reactivated!! </p>");
                    
                var response = await client.SendEmailAsync(msg);
                if (response.IsSuccessStatusCode)
                {
                   log.LogInformation($"Email Successfully delivered to {data.UserId}");
                }
                else
                {
                    log.LogInformation($"Email delivery failed to {data.UserId}");
                }
            }
            catch (Exception ex)
            {
                log.LogInformation($"{ex.Message}");
            }
        }
        
    }

    internal class RequestParams
    {
        public string UserId { get; set; }
    }
}