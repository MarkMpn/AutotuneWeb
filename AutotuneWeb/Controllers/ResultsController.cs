using AutotuneWeb.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Batch;
using Microsoft.Azure.Batch.Auth;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Extensions.Configuration;
using SendGrid;
using SendGrid.Helpers.Mail;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StorageException = Microsoft.Azure.Storage.StorageException;

namespace AutotuneWeb.Controllers
{
    public class ResultsController : Controller
    {
        private readonly ViewRenderService _viewRenderService;

        public ResultsController(ViewRenderService viewRenderService)
        {
            _viewRenderService = viewRenderService;
        }

        public ActionResult Index(string nsUrl)
        {
            var partitionKey = HomeController.GetPartitionKey(nsUrl);

            var connectionString = Startup.Configuration.GetConnectionString("Storage");
            var storageAccount = Microsoft.Azure.Cosmos.Table.CloudStorageAccount.Parse(connectionString);
            var tableClient = storageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("jobs");
            table.CreateIfNotExists();

            var jobs = table.CreateQuery<Job>()
                .Where(j => j.PartitionKey == partitionKey)
                .ToList();

            jobs.Sort((x, y) => y.Timestamp.CompareTo(x.Timestamp));

            ViewBag.NSUrl = nsUrl;
            return View(jobs);
        }

        public ActionResult Details(string nsUrl, string jobId)
        {
            var partitionKey = HomeController.GetPartitionKey(nsUrl);

            // Load the job details
            var connectionString = Startup.Configuration.GetConnectionString("Storage");
            var tableStorageAccount = Microsoft.Azure.Cosmos.Table.CloudStorageAccount.Parse(connectionString);
            var tableClient = tableStorageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("jobs");
            table.CreateIfNotExists();

            var job = table.CreateQuery<Job>()
                .Where(j => j.PartitionKey == partitionKey && j.RowKey == jobId)
                .FirstOrDefault();

            if (job == null)
                return NotFound();

            // Connect to Azure Batch
            var credentials = new BatchSharedKeyCredentials(
                Startup.Configuration["BatchAccountUrl"],
                Startup.Configuration["BatchAccountName"],
                Startup.Configuration["BatchAccountKey"]);

            using (var batchClient = BatchClient.Open(credentials))
            {
                try
                {
                    var jobName = $"autotune-job-{jobId}";

                    // Check if the Autotune job finished successfully
                    var autotune = batchClient.JobOperations.GetTask(jobName, "Autotune");
                    var success = autotune.ExecutionInformation.ExitCode == 0;

                    // Connect to Azure Storage
                    var storageAccount = Microsoft.Azure.Storage.CloudStorageAccount.Parse(connectionString);
                    var cloudBlobClient = storageAccount.CreateCloudBlobClient();

                    // Find the log file produced by Autotune
                    var container = cloudBlobClient.GetContainerReference(jobName);

                    ViewBag.Logs = container.ListBlobs()
                        .Select(b => new CloudBlockBlob(b.Uri, cloudBlobClient))
                        .Where(b => b.Name != "autotune_recommendations.log" && b.Name != "profile.json")
                        .Select(b => b.Name)
                        .ToArray();

                    if (success)
                    {
                        var blob = container.GetBlobReference("autotune_recommendations.log");

                        using (var stream = new MemoryStream())
                        using (var reader = new StreamReader(stream))
                        {
                            // Download the log file
                            blob.DownloadToStream(stream);
                            stream.Position = 0;

                            var recommendations = reader.ReadToEnd();

                            // Parse the results
                            var parsedResults = AutotuneResults.ParseResult(recommendations, job);

                            return View("SuccessDetails", parsedResults);
                        }
                    }
                }
                catch (StorageException ex)
                {
                    if (ex.RequestInformation.ErrorCode == "BlobNotFound")
                        return View("JobExpired");
                }
                catch
                {
                }

                return View("FailureDetails", job);
            }
        }

        public ActionResult DownloadLog(string nsUrl, string jobId, string filename)
        {
            var partitionKey = HomeController.GetPartitionKey(nsUrl);

            // Load the job details
            var connectionString = Startup.Configuration.GetConnectionString("Storage");
            var tableStorageAccount = Microsoft.Azure.Cosmos.Table.CloudStorageAccount.Parse(connectionString);
            var tableClient = tableStorageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("jobs");
            table.CreateIfNotExists();

            var job = table.CreateQuery<Job>()
                .Where(j => j.PartitionKey == partitionKey && j.RowKey == jobId)
                .FirstOrDefault();

            if (job == null)
                return NotFound();

            try
            {
                var jobName = $"autotune-job-{jobId}";

                // Connect to Azure Storage
                var storageAccount = Microsoft.Azure.Storage.CloudStorageAccount.Parse(connectionString);
                var cloudBlobClient = storageAccount.CreateCloudBlobClient();

                // Find the log file produced by Autotune
                var container = cloudBlobClient.GetContainerReference(jobName);

                var blob = container.GetBlobReference(filename);
                var stream = new MemoryStream();
                
                // Download the log file
                blob.DownloadToStream(stream);
                stream.Position = 0;

                return File(stream, "text/plain", filename);
            }
            catch
            {
                return NotFound();
            }
        }

        public async Task<ActionResult> JobFinishedAsync(string partitionKey, string rowKey, string key, string commit)
        {
            // Validate the key
            if (key != Startup.Configuration["ResultsCallbackKey"])
                return NotFound();

            // Load the job details
            var connectionString = Startup.Configuration.GetConnectionString("Storage");
            var tableStorageAccount = Microsoft.Azure.Cosmos.Table.CloudStorageAccount.Parse(connectionString);
            var tableClient = tableStorageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("jobs");
            table.CreateIfNotExists();

            var job = table.CreateQuery<Job>()
                .Where(j => j.PartitionKey == partitionKey && j.RowKey == rowKey)
                .FirstOrDefault();

            if (job == null)
                return NotFound();

            // Connect to Azure Batch
            var credentials = new BatchSharedKeyCredentials(
                Startup.Configuration["BatchAccountUrl"],
                Startup.Configuration["BatchAccountName"],
                Startup.Configuration["BatchAccountKey"]);

            using (var batchClient = BatchClient.Open(credentials))
            {
                var result = "";
                var success = false;
                var startTime = (DateTime?)null;
                var endTime = (DateTime?)null;
                var emailBody = (string)null;
                var attachments = new CloudBlob[0];
                
                try
                {
                    var jobName = $"autotune-job-{rowKey}";

                    // Check if the Autotune job finished successfully
                    var autotune = batchClient.JobOperations.GetTask(jobName, "Autotune");
                    success = autotune.ExecutionInformation.ExitCode == 0;
                    startTime = autotune.ExecutionInformation.StartTime;
                    endTime = autotune.ExecutionInformation.EndTime;
                    
                    // Connect to Azure Storage
                    var storageAccount = Microsoft.Azure.Storage.CloudStorageAccount.Parse(connectionString);
                    var cloudBlobClient = storageAccount.CreateCloudBlobClient();

                    // Find the log file produced by Autotune
                    var container = cloudBlobClient.GetContainerReference(jobName);

                    if (success)
                    {
                        var blob = container.GetBlobReference("autotune_recommendations.log");

                        using (var stream = new MemoryStream())
                        using (var reader = new StreamReader(stream))
                        {
                            // Download the log file
                            blob.DownloadToStream(stream);
                            stream.Position = 0;

                            var recommendations = reader.ReadToEnd();

                            // Parse the results
                            var parsedResults = AutotuneResults.ParseResult(recommendations, job);
                            parsedResults.Commit = commit;

                            emailBody = await _viewRenderService.RenderToStringAsync("Results/Success", parsedResults);
                        }
                    }

                    // Get the log files to attach to the email
                    attachments = container.ListBlobs()
                        .Select(b => new CloudBlockBlob(b.Uri, cloudBlobClient))
                        .Where(b => b.Name != "autotune_recommendations.log" && b.Name != "profile.json")
                        .ToArray();
                }
                catch (Exception ex)
                {
                    result = ex.ToString();
                    success = false;
                }

                if (emailBody == null)
                {
                    emailBody = await _viewRenderService.RenderToStringAsync("Results/Failure", commit);
                }

                EmailResults(job.EmailResultsTo, emailBody, attachments);

                // Update the details in the table
                job.ProcessingStarted = startTime;
                job.ProcessingCompleted = endTime;
                job.Result = result;
                job.Failed = !success;
                var update = TableOperation.Replace(job);
                await table.ExecuteAsync(update);

                // Store the commit hash to identify the version of Autotune that was used
                var commitSetting = new Settings();
                commitSetting.PartitionKey = "Commit";
                commitSetting.RowKey = "";
                commitSetting.Value = commit;
                var settingTable = tableClient.GetTableReference("settings");
                settingTable.CreateIfNotExists();
                var upsert = TableOperation.InsertOrReplace(commitSetting);
                settingTable.Execute(upsert);
            }

            return Content("");
        }

        private void EmailResults(string emailAddress, string emailBody, CloudBlob[] attachments)
        {
            var apiKey = Startup.Configuration["SendGridApiKey"];
            var client = new SendGridClient(apiKey);
            var from = new EmailAddress(Startup.Configuration["SendGridFromAddress"], "Autotune");
            var subject = "Autotune Results";
            var to = new EmailAddress(emailAddress);
            var msg = MailHelper.CreateSingleEmail(from, to, subject, "", emailBody);

            foreach (var attachment in attachments)
            {
                using (var stream = new MemoryStream())
                {
                    attachment.DownloadToStream(stream);
                    msg.AddAttachment(attachment.Name, Convert.ToBase64String(stream.ToArray()));
                }
            }
            
            client.SendEmailAsync(msg).Wait();
        }
    }
}