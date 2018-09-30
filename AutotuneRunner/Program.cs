using SendGrid;
using SendGrid.Helpers.Mail;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AutotuneRunner
{
    class Program
    {
        static void Main(string[] args)
        {
            // Connect to the SQL server
            using (var con = new SqlConnection(Environment.GetEnvironmentVariable("AUTOTUNE_CONNECTIONSTRING")))
            using (var cmd = con.CreateCommand())
            {
                con.Open();

                // Load in the jobs that haven't been run yet
                cmd.CommandText = "SELECT TOP 1 JobID, NSUrl, Profile, PumpBasalIncrement, EmailResultsTo, Units, CategorizeUAMAsBasal FROM Jobs WHERE ProcessingStarted IS NULL ORDER BY JobID";

                var profilesToRun = new List<Job>();

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var jobId = reader.GetInt32(0);
                        var nsUrl = reader.GetString(1);
                        var profile = reader.GetString(2);
                        var pumpBasalIncrement = reader.GetDecimal(3);
                        var email = reader.GetString(4);
                        var units = reader.GetString(5);
                        var uamAsBasal = reader.GetBoolean(6);

                        profilesToRun.Add(new Job
                        {
                            JobID = jobId,
                            NSUrl = nsUrl,
                            Profile = profile,
                            PumpBasalIncrement = pumpBasalIncrement,
                            EmailResultsTo = email,
                            Units = units,
                            UAMAsBasal = uamAsBasal
                        });
                    }
                }

                var jobIdParam = cmd.CreateParameter();
                jobIdParam.ParameterName = "@JobID";
                cmd.Parameters.Add(jobIdParam);

                var resultParam = cmd.CreateParameter();
                resultParam.ParameterName = "@Result";

                var failedParam = cmd.CreateParameter();
                failedParam.ParameterName = "@Failed";

                // Run each one in turn
                foreach (var job in profilesToRun)
                {
                    jobIdParam.Value = job.JobID;

                    var result = "";
                    var failed = false;

                    try
                    {
                        // Indicate that we are now running the job
                        cmd.CommandText = "UPDATE Jobs SET ProcessingStarted = CURRENT_TIMESTAMP WHERE JobID = @JobID";
                        cmd.ExecuteNonQuery();

                        // Save the profile to disk
                        var autotunePath = Path.Combine(Environment.GetEnvironmentVariable("AUTOTUNE_ROOT"), "autotunerunner-job-" + job.JobID);
                        var settingsPath = Path.Combine(autotunePath, "settings");
                        Directory.CreateDirectory(settingsPath);
                        foreach (var filename in new[] { "autotune.json", "profile.json", "pumpprofile.json" })
                        {
                            File.WriteAllText(Path.Combine(settingsPath, filename), job.Profile);
                        }

                        Environment.CurrentDirectory = autotunePath;

                        var startDate = DateTime.Today.AddDays(-7);
                        var endDate = DateTime.Today.AddDays(-1);
                        var daysTotal = 7;

                        if (!File.Exists("autotune/autotune_recommendations.log"))
                        {
                            // Remove all files currently in the autotune directory
                            "rm -f ./autotune/*.*".Bash();

                            // Run Autotune
                            var cmdLine = $"oref0-autotune --dir={autotunePath} --ns-host={job.NSUrl} --start-date={startDate:yyyy-MM-dd} --end-date={endDate:yyyy-MM-dd} --categorize-uam-as-basal={(job.UAMAsBasal ? "true" : "false")}";
                            result = cmdLine;
                            cmdLine.Bash();
                        }

                        // Wait for the file to exist
                        for (var i = 0; i < 10; i++)
                        {
                            if (File.Exists("autotune/autotune_recommendations.log"))
                                break;

                            System.Threading.Thread.Sleep(1000);
                        }

                        result = File.ReadAllText("autotune/autotune_recommendations.log");

                        // Parse the results
                        var parsedResults = ParseResult(result, job.PumpBasalIncrement);

                        // If we want to get results in mmol, convert now
                        if (job.Units == "mmol")
                        {
                            parsedResults.PumpISF /= 18;
                            parsedResults.AutotuneISF /= 18;
                        }

                        // Generate the HTML email for the results
                        var header = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Header.html"));
                        var middle = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Middle.html"));
                        var footer = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Footer.html"));

                        var intro = $"<h2><a href=\"{job.NSUrl}\">{job.NSUrl}</a></h2>";
                        intro += $"<p>Based on data entered between {startDate:yyyy-MM-dd} and {endDate:yyyy-MM-dd}</p>";

                        if (job.UAMAsBasal)
                            intro += "<p>Unannounced meals were ignored and counted towards basal recommendations. If not all carbs were recorded, re-run with the UAM as Basals option disabled.</p>";
                        else
                            intro += "<p>Sudden rises were counted as being triggered by carbs that were not recorded rather than incorrect basals. If all carbs were recorded, re-run with the UAM as Basals option enabled.</p>";

                        var units = job.Units == "mmol" ? "mmol/L" : "mg/dL";
                        var isfcr = $@"
                            <table width='50%'>
                                <thead>
                                    <tr>
                                        <th style='border-top: solid 1px #ddd; padding: 8px'>Parameter</th>
                                        <th style='border-top: solid 1px #ddd; padding: 8px'>Original&nbsp;Value</th>
                                        <th style='border-top: solid 1px #ddd; padding: 8px'>Autotune&nbsp;Result</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    <tr style='{GetStripedBackground(0)}'>
                                        <th style='border-top: solid 1px #ddd; padding: 8px'>ISF ({units}/U)</th>
                                        <td style='border-top: solid 1px #ddd; padding: 8px'>{parsedResults.PumpISF:0.0}</td>
                                        <td style='border-top: solid 1px #ddd; padding: 8px; {GetBackground(parsedResults.PumpISF, parsedResults.AutotuneISF)}'>{parsedResults.AutotuneISF:0.0}</td>
                                    </tr>
                                    <tr>
                                        <th style='border-top: solid 1px #ddd; padding: 8px'>CR (g/U)</th>
                                        <td style='border-top: solid 1px #ddd; padding: 8px'>{parsedResults.PumpCR:0.0}</td>
                                        <td style='border-top: solid 1px #ddd; padding: 8px; {GetBackground(parsedResults.PumpCR, parsedResults.AutotuneCR)}'>{parsedResults.AutotuneCR:0.0}</td>
                                    </tr>
                            </table>
                        ";

                        var basals = $@"
                            <table width='50%' cellspacing='0'>
                                <thead>
                                    <tr>
                                        <th width='25%' style='border-top: solid 1px #ddd; padding: 8px'>Time</th>
                                        <th width='25%' style='border-top: solid 1px #ddd; padding: 8px'>Original</th>
                                        <th width='25%' style='border-top: solid 1px #ddd; padding: 8px'>Autotune&nbsp;Result</th>
                                        <th width='25%' style='border-top: solid 1px #ddd; padding: 8px'>Rounded&nbsp;to&nbsp;{job.PumpBasalIncrement}</th>
                                    </tr>
                                </thead>
                                <tbody>" +
                                String.Join("\r\n", Enumerable.Range(0, 24).Select(hr => $@"
                                    <tr style='{GetStripedBackground(hr)}'>
                                        <th style='border-top: solid 1px #ddd; padding: 8px'>{hr:00}:00</th>
                                        <td style='border-top: solid 1px #ddd; padding: 8px'>{parsedResults.PumpBasals[hr]:0.000}</td>
                                        <td style='border-top: solid 1px #ddd; padding: 8px'>{parsedResults.AutotuneBasals[hr]:0.000}</td>
                                        <td style='border-top: solid 1px #ddd; padding: 8px; {GetBackground(parsedResults.PumpBasals[hr], parsedResults.SuggestedBasals[hr])}'>{parsedResults.SuggestedBasals[hr]:0.000} {BasalWarning(daysTotal, parsedResults.DaysMissed[hr])}</td>
                                    </tr>
                                ")) +
                                $@"</tbody>
                                <tfoot>
                                    <tr>
                                        <th>Daily Total</th>
                                        <th style='border-top: solid 1px #ddd; padding: 8px'>{parsedResults.PumpBasals.Sum():0.000}</th>
                                        <th style='border-top: solid 1px #ddd; padding: 8px'>{parsedResults.AutotuneBasals.Sum():0.000}</th>
                                        <th style='border-top: solid 1px #ddd; padding: 8px'>{parsedResults.SuggestedBasals.Sum():0.000}</th>
                                    </tr>
                                </tfoot>
                            </table>";

                        File.WriteAllText("autotune/autotune.html", header + intro + isfcr + middle + basals + footer);

                        // Email the results
                        SendResults(job.EmailResultsTo, "autotune/autotune.html", "autotune/autotune.*.log");

                        // TODO: Tidy up
                    }
                    catch (Exception ex)
                    {
                        failed = true;

                        // Email an error
                        var header = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Header.html"));
                        var body = "<p>Sorry, an error occurred while running Autotune.</p><p>Sometimes, re-running the job might get it to work on the second try. If it still fails there's probably something else going wrong, so try reaching out on Facebook for help.</p>";
                        var footer = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Footer.html"));
                        File.WriteAllText("autotune/autotune.html", header + body + footer);

                        SendResults(job.EmailResultsTo, "autotune/autotune.html", "autotune/autotune.*.log");

                        result = ex.ToString() + "\r\n\r\n" + result;
                    }

                    // Save the results to the database
                    cmd.CommandText = "UPDATE Jobs SET ProcessingCompleted = CURRENT_TIMESTAMP, Result = @Result, Failed = @Failed WHERE JobID = @JobID";
                    resultParam.Value = result;
                    cmd.Parameters.Add(resultParam);
                    failedParam.Value = failed;
                    cmd.Parameters.Add(failedParam);
                    cmd.ExecuteNonQuery();
                    cmd.Parameters.Remove(resultParam);
                    cmd.Parameters.Remove(failedParam);
                }
            }
        }

        private static string GetStripedBackground(int hr)
        {
            if ((hr % 2) == 0)
                return "background-color: #f9f9f9";

            return "";
        }

        private static string BasalWarning(int daysTotal, int daysMissed)
        {
            var days = new List<string>();

            for (var i = 0; i < daysTotal; i++)
            {
                days.Add($"<div style=\"display: inline-block; height: 5px; width: 5px; margin-left: 1px; background-color: {(i < (daysTotal - daysMissed) ? "green" : "red")}\"></div>");
            }

            return String.Join("", days);
        }

        private static void SendResults(string emailAddress, string filename, params string[] attachments)
        {
            var apiKey = Environment.GetEnvironmentVariable("SENDGRID_API_KEY");
            var client = new SendGridClient(apiKey);
            var from = new EmailAddress(Environment.GetEnvironmentVariable("SENDGRID_FROM_ADDRESS"), "Autotune");
            var subject = "Autotune Results";
            var to = new EmailAddress(emailAddress);
            var html = File.ReadAllText(filename);
            var msg = MailHelper.CreateSingleEmail(from, to, subject, "", html);
            
            if (attachments != null)
            {
                foreach (var pattern in attachments)
                {
                    foreach (var file in Directory.GetFiles(Path.GetDirectoryName(pattern), Path.GetFileName(pattern)))
                        msg.AddAttachment(Path.GetFileName(file), Convert.ToBase64String(File.ReadAllBytes(file)));
                }
            }

            client.SendEmailAsync(msg).Wait();
        }

        private static string GetBackground(decimal old, decimal updated)
        {
            var delta = updated - old;
            var percentage = delta * 100 / old;

            if (percentage >= 19 ||
                percentage <= -29)
                return "background-color: rgb(242, 222, 222);";

            if (percentage >= 10 ||
                percentage <= -10)
                return "background-color: rgb(252, 248, 227);";

            return "";
        }

        private static AutotuneResults ParseResult(string result, decimal basalIncrement)
        {
            // Split the output into lines
            var lines = result.Trim().Split('\n');

            // The final output is in a table at the end.
            var pumpOutput = new Dictionary<string, decimal?>();
            var autotuneOutput = new Dictionary<string, decimal>();
            var daysMissed = new Dictionary<string, int>();

            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();

                if (line.StartsWith("-------------------------------------"))
                    break;

                if (line.StartsWith("Basals [U/hr]"))
                    continue;

                var parts = line.Split('|').Select(part => part.Trim()).ToArray();

                if (parts[0].EndsWith(":30"))
                    continue;

                try
                {
                    if (!String.IsNullOrEmpty(parts[1]))
                        pumpOutput[parts[0]] = Decimal.Parse(parts[1]);
                    else
                        pumpOutput[parts[0]] = null;

                }
                catch (FormatException ex)
                {
                    throw new ApplicationException($"Error parsing value [{parts[1]}] from line [{line}]", ex);
                }

                try
                {
                    autotuneOutput[parts[0]] = Decimal.Parse(parts[2]);
                }
                catch (FormatException ex)
                {
                    throw new ApplicationException($"Error parsing value [{parts[2]}] from line [{line}]", ex);
                }

                if (parts.Length > 3 && !String.IsNullOrEmpty(parts[3]))
                {
                    try
                    {
                        daysMissed[parts[0]] = Int32.Parse(parts[3]);
                    }
                    catch (FormatException ex)
                    {
                        throw new ApplicationException($"Error parsing value [{parts[3]}] from line [{line}]", ex);
                    }
                }
            }

            var output = new AutotuneResults();
            output.PumpISF = pumpOutput["ISF [mg/dL/U]"].Value;
            output.AutotuneISF = autotuneOutput["ISF [mg/dL/U]"];
            output.PumpCR = pumpOutput["Carb Ratio[g/U]"].Value;
            output.AutotuneCR = autotuneOutput["Carb Ratio[g/U]"];
            output.PumpBasals = new decimal[24];
            output.AutotuneBasals = new decimal[24];
            output.SuggestedBasals = new decimal[24];
            output.DaysMissed = new int[24];

            for (var hour = 0; hour < 24; hour++)
            {
                output.PumpBasals[hour] = pumpOutput[$"{hour:00}:00"] ?? output.PumpBasals[hour - 1];
                output.AutotuneBasals[hour] = autotuneOutput[$"{hour:00}:00"];
                output.SuggestedBasals[hour] = Math.Round(output.AutotuneBasals[hour] / basalIncrement) * basalIncrement;
                output.DaysMissed[hour] = daysMissed[$"{hour:00}:00"];
            }

            return output;
        }
    }
}
