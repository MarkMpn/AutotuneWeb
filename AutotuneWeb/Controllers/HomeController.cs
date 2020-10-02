using AutotuneWeb.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Batch;
using Microsoft.Azure.Batch.Auth;
using Microsoft.Azure.Batch.Common;
using Microsoft.Azure.Cosmos.Table;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace AutotuneWeb.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            var url = Request.Cookies["nsUrl"];
            return View((object)url);
        }

        [HttpPost]
        public ActionResult Autotune(Uri nsUrl, decimal? cr, decimal? sens)
        {
            if (nsUrl == null)
            {
                ModelState.AddModelError(nameof(nsUrl), "Nightscout URL is required");
                return View("Index");
            }

            Response.Cookies.Append("nsUrl", nsUrl.ToString());
            NSProfileDetails nsProfile;
            DateTime profileActivation;

            try
            {
                nsProfile = NSProfileDetails.LoadFromNightscout(ref nsUrl, out profileActivation);
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(nameof(nsUrl), ex.Message);
                return View("Index", nsUrl.ToString());
            }

            ModelState.SetModelValue(nameof(nsUrl), nsUrl, nsUrl.ToString());
            ViewBag.NSUrl = nsUrl;
            ViewBag.ProfileActivation = profileActivation;
            ViewBag.PreviousResults = HasPreviousResults(nsUrl);

            nsProfile.CarbRatio = CombineAdjacentTimeBlocks(nsProfile.CarbRatio);
            nsProfile.Sensitivity = CombineAdjacentTimeBlocks(nsProfile.Sensitivity);

            ViewBag.Units = nsProfile.Units;
            ViewBag.ProfileName = nsProfile.Name;

            if (cr != null)
                nsProfile.CarbRatio = new[] { new NSValueWithTime { TimeAsSeconds = 0, Value = cr.Value } };

            if (sens != null)
                nsProfile.Sensitivity = new[] { new NSValueWithTime { TimeAsSeconds = 0, Value = sens.Value } };

            // Check for multiple IC and ISF ratios
            if (nsProfile.CarbRatio.Length > 1 || nsProfile.Sensitivity.Length > 1)
                return View("MultipleValues", nsProfile);

            var oapsProfile = nsProfile.MapToOaps();

            var warnings = new List<string>();

            if (oapsProfile.Dia < 3)
            {
                warnings.Add($"DIA of {oapsProfile.Dia} hours is too short. It has been automatically adjusted to the minimum of 3 hours");
                oapsProfile.Dia = 3;
            }

            if (!TempBasalIncludesRateProperty(nsUrl))
                warnings.Add("Temporary basal records in this Nightscout instance do not include a \"rate\" property - they will not be taken into account by Autotune");

            ViewBag.Warnings = warnings;
            ViewBag.TimeZone = nsProfile.TimeZone;
            ViewBag.Email = Request.Cookies["email"];
            return View("Converted", oapsProfile);
        }

        private bool HasPreviousResults(Uri nsUrl)
        {
            var connectionString = Startup.Configuration.GetConnectionString("Storage");
            var storageAccount = Microsoft.Azure.Cosmos.Table.CloudStorageAccount.Parse(connectionString);
            var tableClient = storageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("jobs");
            table.CreateIfNotExists();
            var partitionKey = GetPartitionKey(nsUrl.ToString());

            var existing = table.CreateQuery<Job>()
                .Where(j => j.PartitionKey == partitionKey)
                .FirstOrDefault();

            return existing != null;
        }

        class NSTempBasal
        {
            public decimal? Rate { get; set; }
        }

        private bool TempBasalIncludesRateProperty(Uri url)
        {
            var profileSwitchUrl = new Uri(url, "/api/v1/treatments.json?find[eventType][$eq]=Temp%20Basal&count=10");
            var req = WebRequest.CreateHttp(profileSwitchUrl);
            using (var resp = req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                var json = reader.ReadToEnd();
                var tempBasals = JsonConvert.DeserializeObject<NSTempBasal[]>(json);

                return tempBasals.Length == 0 || tempBasals.Any(tb => tb.Rate != null);
            }
        }

        private NSValueWithTime[] CombineAdjacentTimeBlocks(NSValueWithTime[] values)
        {
            var combined = new List<NSValueWithTime>();

            for (var i = 0; i < values.Length; i++)
            {
                if (i == 0 || combined.Last().Value != values[i].Value)
                    combined.Add(values[i]);
            }

            return combined.ToArray();
        }

        public async Task<ActionResult> RunJob(Uri nsUrl, string oapsProfile, string units, string timezone, bool? uamAsBasal, double pumpBasalIncrement, decimal? min5MCarbImpact, string curve, string emailResultsTo, int days)
        {
            if (min5MCarbImpact != null || !String.IsNullOrEmpty(curve))
            {
                var profile = JsonConvert.DeserializeObject<OapsProfile>(oapsProfile);

                if (min5MCarbImpact != null)
                    profile.Min5mCarbImpact = min5MCarbImpact.Value;

                if (!String.IsNullOrEmpty(curve))
                    profile.InsulinCurve = curve;

                oapsProfile = JsonConvert.SerializeObject(profile);
            }

            // Cap the number of days to run Autotune over
            days = Math.Min(days, 30);

            // Save the details of this job in the database
            var job = new Job
            {
                PartitionKey = GetPartitionKey(nsUrl.ToString()),
                NSUrl = nsUrl.ToString(),
                Units = units,
                TimeZone = timezone,
                UAMAsBasal = uamAsBasal.GetValueOrDefault(),
                PumpBasalIncrement = pumpBasalIncrement,
                EmailResultsTo = emailResultsTo,
                Days = days
            };

            // Check if the same job is already running
            var connectionString = Startup.Configuration.GetConnectionString("Storage");
            var storageAccount = Microsoft.Azure.Cosmos.Table.CloudStorageAccount.Parse(connectionString);
            var tableClient = storageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("jobs");
            table.CreateIfNotExists();

            var existing = table.CreateQuery<Job>()
                .Where(j => j.PartitionKey == job.PartitionKey && !(j.ProcessingCompleted < DateTime.Now || j.ProcessingCompleted > DateTime.Now))
                .FirstOrDefault();

            int queuePos;

            if (existing != null)
            {
                // Get the job details from Azure Batch
                var credentials = new BatchSharedKeyCredentials(
                    Startup.Configuration["BatchAccountUrl"],
                    Startup.Configuration["BatchAccountName"],
                    Startup.Configuration["BatchAccountKey"]);

                DateTime? existingJobStarted = null;

                using (var batchClient = BatchClient.Open(credentials))
                {
                    var existingJobName = $"autotune-job-{existing.RowKey}";

                    try
                    {
                        var existingTask = batchClient.JobOperations.GetTask(existingJobName, "Autotune");

                        existingJobStarted = existingTask.ExecutionInformation.StartTime;
                        queuePos = batchClient.JobOperations.ListJobs(new ODATADetailLevel(filterClause: "state eq 'active'", selectClause: "id")).Count();

                        if (existingJobStarted == null)
                            ViewBag.QueuePos = queuePos;

                        ViewBag.JobStarted = existingJobStarted;
                        return View("AlreadyRunning", new Job { PartitionKey = job.PartitionKey, RowKey = existing.RowKey });
                    }
                    catch (BatchException)
                    {
                        // Job does not exist in Batch account, may have been deleted. Mark this job as completed and move on to creating a new job
                        existing.Failed = true;
                        existing.ProcessingCompleted = DateTime.Now;
                        existing.Result = "Task missing from Batch account";
                        table.Execute(TableOperation.Replace(existing));
                    }
                }
            }

            job.RowKey = Guid.NewGuid().ToString();
            var insert = TableOperation.Insert(job);
            table.Execute(insert);

            var jobName = $"autotune-job-{job.RowKey}";
            var storageLocation = await SaveProfileToStorageAsync(jobName, oapsProfile);
            queuePos = await CreateBatchJobAsync(jobName, job, storageLocation.ProfileUrl, storageLocation.ContainerUrl);

            // Keep track of how many jobs have been processed, in total and per day
            UpdateStats(tableClient);

            Response.Cookies.Append("email", emailResultsTo);

            return RedirectToAction("Index", "Results", new { nsUrl = nsUrl.ToString() });
        }

        private void UpdateStats(CloudTableClient tableClient)
        {
            var table = tableClient.GetTableReference("stats");
            table.CreateIfNotExists();

            IncrementJobCount(table, "Total");
            IncrementJobCount(table, DateTime.Now.ToString("yyyy-MM-dd"));
        }

        private void IncrementJobCount(CloudTable table, string rowKey)
        {
            while (true)
            {
                var existing = table.CreateQuery<Statistics>()
                    .Where(s => s.PartitionKey == "JobCount" && s.RowKey == rowKey)
                    .FirstOrDefault();

                try
                {
                    if (existing == null)
                    {
                        var stat = new Statistics
                        {
                            PartitionKey = "JobCount",
                            RowKey = rowKey,
                            JobCount = 1
                        };

                        var insert = TableOperation.Insert(stat);
                        table.Execute(insert);
                    }
                    else
                    {
                        existing.JobCount++;
                        var update = TableOperation.Replace(existing);
                        table.Execute(update);
                    }

                    return;
                }
                catch (Microsoft.Azure.Cosmos.Table.StorageException ex)
                {
                    if (ex.RequestInformation.HttpStatusCode == 412)
                        continue;

                    throw;
                }
            }
        }

        internal static string GetPartitionKey(string nsUrl)
        {
            using (var sha1 = SHA1.Create())
            {
                return BitConverter.ToString(sha1.ComputeHash(Encoding.UTF8.GetBytes(nsUrl))).ToLower().Replace("-", "");
            }
        }

        class StorageLocation
        {
            public string ContainerUrl { get; set; }

            public string ProfileUrl { get; set; }
        }

        private async Task<StorageLocation> SaveProfileToStorageAsync(string jobName, string profile)
        {
            var location = new StorageLocation();

            // Connect to Azure Storage
            var connectionString = Startup.Configuration.GetConnectionString("Storage");
            var storageAccount = Microsoft.Azure.Storage.CloudStorageAccount.Parse(connectionString);
            var cloudBlobClient = storageAccount.CreateCloudBlobClient();

            // Create container for the job
            var container = cloudBlobClient.GetContainerReference(jobName);
            await container.CreateAsync();

            // Get a SAS URL for the container. This should allow write access to the container for the next 24 hours only
            // to allow the job to upload results
            var containerSasConstrains = new SharedAccessBlobPolicy
            {
                SharedAccessStartTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddHours(24),
                Permissions = SharedAccessBlobPermissions.Read | SharedAccessBlobPermissions.Write
            };

            // Generate the shared access signature on the container, setting the constraints directly on the signature
            var sasContainerToken = container.GetSharedAccessSignature(containerSasConstrains);
            location.ContainerUrl = container.Uri + sasContainerToken;

            // Create the profile.json blob in the container
            var blob = container.GetBlockBlobReference("profile.json");
            await blob.UploadTextAsync(profile);

            // Get a SAS URL for the blob. This should allow read access to the profile for the next 24 hours only
            var blobSasConstraints = new SharedAccessBlobPolicy
            {
                SharedAccessStartTime = DateTimeOffset.UtcNow.AddMinutes(-5),
                SharedAccessExpiryTime = DateTimeOffset.UtcNow.AddHours(24),
                Permissions = SharedAccessBlobPermissions.Read
            };

            // Generate the shared access signature on the blob, setting the constraints directly on the signature.
            var sasBlobToken = blob.GetSharedAccessSignature(blobSasConstraints);

            // Return the URI string for the blob, including the SAS token.
            location.ProfileUrl = blob.Uri + sasBlobToken;

            return location;
        }

        private async Task<int> CreateBatchJobAsync(string jobName, Job jobDetails, string profileUrl, string containerUrl)
        {
            // Connect to Azure Batch
            var credentials = new BatchSharedKeyCredentials(
                Startup.Configuration["BatchAccountUrl"],
                Startup.Configuration["BatchAccountName"],
                Startup.Configuration["BatchAccountKey"]);

            using (var batchClient = BatchClient.Open(credentials))
            {
                var poolId = Startup.Configuration["BatchPoolId"];

                // Create the job
                var job = batchClient.JobOperations.CreateJob();
                job.Id = jobName;
                job.PoolInformation = new PoolInformation { PoolId = poolId };
                job.UsesTaskDependencies = true;
                job.OnAllTasksComplete = OnAllTasksComplete.TerminateJob;
                await job.CommitAsync();

                // Add a task to the job to run Autotune
                var commandLine = "/bin/sh -c '" +
                    "cd \"$AZ_BATCH_TASK_WORKING_DIR\" && " +
                    "mkdir -p settings && " +
                    "mv profile.json settings && " +
                    "cp settings/profile.json settings/pumpprofile.json && " +
                    "cp settings/profile.json settings/autotune.json && " +
                    $"TZ='{jobDetails.TimeZone}' && " +
                    "export TZ && " +
                    $"oref0-autotune --dir=$AZ_BATCH_TASK_WORKING_DIR --ns-host={jobDetails.NSUrl} --start-date={DateTime.Now.AddDays(-jobDetails.Days):yyyy-MM-dd} --end-date={DateTime.Now.AddDays(-1):yyyy-MM-dd} --categorize-uam-as-basal={(jobDetails.UAMAsBasal ? "true" : "false")}" +
                    "'";

                var task = new CloudTask("Autotune", commandLine);

                // The task needs to use the profile.json file previously added to Azure Storage
                task.ResourceFiles = new List<ResourceFile>();
                task.ResourceFiles.Add(ResourceFile.FromUrl(profileUrl, "profile.json"));

                // Capture the recommendations generated by Autotune into Azure Storage
                task.OutputFiles = new List<OutputFile>();
                task.OutputFiles.Add(new OutputFile(
                    filePattern: "autotune/autotune_recommendations.log", 
                    destination: new OutputFileDestination(new OutputFileBlobContainerDestination(containerUrl)), 
                    uploadOptions: new OutputFileUploadOptions(OutputFileUploadCondition.TaskCompletion)));
                task.OutputFiles.Add(new OutputFile(
                    filePattern: "autotune/autotune.*.log",
                    destination: new OutputFileDestination(new OutputFileBlobContainerDestination(containerUrl)),
                    uploadOptions: new OutputFileUploadOptions(OutputFileUploadCondition.TaskCompletion)));

                // Ensure the task still completes even if Autotune fails, as we still want to email the
                // log file with the error details
                task.ExitConditions = new ExitConditions
                {
                    ExitCodes = new List<ExitCodeMapping>(new[] 
                    {
                        new ExitCodeMapping(1, new ExitOptions { DependencyAction = DependencyAction.Satisfy })
                    })
                };

                await batchClient.JobOperations.AddTaskAsync(jobName, task);

                // Get the URL for the JobFinished action that the recommendations can be uploaded to to generate the email
                var uploadUrl = Url.Action("JobFinished", "Results", new { partitionKey = jobDetails.PartitionKey, rowKey = jobDetails.RowKey, key = Startup.Configuration["ResultsCallbackKey"] }, Request.Scheme);
                var uploadCommandLine = $"/bin/sh -c '" +
                    "cd /usr/src/oref0 && " +
                    $"wget -O /dev/null -o /dev/null {uploadUrl.Replace("&", "\\&")}\\&commit=$(git rev-parse --short HEAD)" +
                    "'";
                var uploadTask = new CloudTask("Upload", uploadCommandLine);
                uploadTask.DependsOn = TaskDependencies.OnId(task.Id);
                uploadTask.Constraints = new TaskConstraints(maxTaskRetryCount: 2);
                await batchClient.JobOperations.AddTaskAsync(jobName, uploadTask);

                var queuePos = batchClient.JobOperations.ListJobs(new ODATADetailLevel(filterClause: "state eq 'active'", selectClause: "id")).Count();
                return queuePos;
            }
        }

        public async Task<ActionResult> About()
        {
            // Get the details of the Autotune commit used for the latest job
            var connectionString = Startup.Configuration.GetConnectionString("Storage");
            var storageAccount = Microsoft.Azure.Cosmos.Table.CloudStorageAccount.Parse(connectionString);
            var tableClient = storageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("settings");
            await table.CreateIfNotExistsAsync();

            var commit = table.CreateQuery<Settings>()
                .Where(s => s.PartitionKey == "Commit" && s.RowKey == "")
                .SingleOrDefault();

            ViewBag.Commit = commit?.Value ?? "";

            return View();
        }

        public ActionResult MissingRate()
        {
            return View();
        }

        public ActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public ActionResult Error()
        {
            return View();
        }

        public async Task<ActionResult> Delete(string url, string email)
        {
            var connectionString = Startup.Configuration.GetConnectionString("Storage");
            var storageAccount = Microsoft.Azure.Cosmos.Table.CloudStorageAccount.Parse(connectionString);
            var tableClient = storageAccount.CreateCloudTableClient();
            var table = tableClient.GetTableReference("jobs");
            await table.CreateIfNotExistsAsync();

            var query = table.CreateQuery<Job>()
                .Where(
                    TableQuery.CombineFilters(
                        TableQuery.GenerateFilterCondition(nameof(Job.PartitionKey), QueryComparisons.Equal, GetPartitionKey(url)),
                        TableOperators.And,
                        TableQuery.GenerateFilterCondition(nameof(Job.EmailResultsTo), QueryComparisons.Equal, email)
                    )
                )
                .Select(new[] { nameof(Job.RowKey) });

            TableContinuationToken continuationToken = null;
            var deleted = 0;

            do
            {
                var result = await table.ExecuteQuerySegmentedAsync(query, continuationToken);
                continuationToken = result.ContinuationToken;

                var chunks = result.Results.Select((j, index) => new { Index = index, Job = j })
                    .GroupBy(x => x.Index / 100)
                    .Select(g => g.Select(x => x.Job).ToList())
                    .ToList();

                foreach (var chunk in chunks)
                {
                    var batch = new TableBatchOperation();

                    foreach (var job in chunk)
                        batch.Add(TableOperation.Delete(job));

                    await table.ExecuteBatchAsync(batch);
                }
            } while (continuationToken != null);

            return View(deleted);
        }
    }
}