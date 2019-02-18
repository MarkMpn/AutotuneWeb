using AutotuneWeb.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using System.Web.Mvc;

namespace AutotuneWeb.Controllers
{
    public class HomeController : Controller
    {
        public ActionResult Index()
        {
            var url = Request.Cookies["nsUrl"]?.Value;
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

            if (nsUrl.Scheme != "https")
            {
                ModelState.AddModelError(nameof(nsUrl), "Please use https for your Nightscout URL");
                return View("Index");
            }

            Response.Cookies.Add(new HttpCookie("nsUrl", nsUrl.ToString()));
            ViewBag.NSUrl = nsUrl;
            NSProfileDetails nsProfile;

            try
            {
                nsProfile = NSProfileDetails.LoadFromNightscout(nsUrl);
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(nameof(nsUrl), ex.Message);
                return View("Index");
            }

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
            ViewBag.Email = Request.Cookies["email"]?.Value;
            return View("Converted", oapsProfile);
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

        public ActionResult RunJob(Uri nsUrl, string oapsProfile, string units, string timezone, bool? uamAsBasal, decimal pumpBasalIncrement, decimal? min5MCarbImpact, string curve, string emailResultsTo, int days)
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
            using (var con = new SqlConnection(ConfigurationManager.ConnectionStrings["Sql"].ConnectionString))
            using (var cmd = con.CreateCommand())
            {
                con.Open();

                cmd.CommandText = "INSERT INTO Jobs (NSUrl, Profile, CreatedAt, Units, TimeZone, CategorizeUAMAsBasal, PumpBasalIncrement, EmailResultsTo, DaysDuration) VALUES (@NSUrl, @Profile, CURRENT_TIMESTAMP, @Units, @TimeZone, @UAMAsBasal, @PumpBasalIncrement, @EmailResultsTo, @Days)";

                cmd.Parameters.AddWithValue("@NSUrl", nsUrl.ToString());
                cmd.Parameters.AddWithValue("@Profile", oapsProfile.Replace("\r\n", "\n"));
                cmd.Parameters.AddWithValue("@Units", units);
                cmd.Parameters.AddWithValue("@TimeZone", timezone);
                cmd.Parameters.AddWithValue("@UAMAsBasal", uamAsBasal.GetValueOrDefault());
                cmd.Parameters.AddWithValue("@PumpBasalIncrement", pumpBasalIncrement);
                cmd.Parameters.AddWithValue("@EmailResultsTo", emailResultsTo);
                cmd.Parameters.AddWithValue("@Days", days);

                cmd.ExecuteNonQuery();
            }

            Response.Cookies.Add(new HttpCookie("email", emailResultsTo));

            return View();
        }

        public ActionResult About()
        {
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

        public ActionResult Delete(string url, string email)
        {
            using (var con = new SqlConnection(ConfigurationManager.ConnectionStrings["Sql"].ConnectionString))
            using (var cmd = con.CreateCommand())
            {
                con.Open();

                cmd.CommandText = "DELETE FROM Jobs WHERE NSUrl = @url AND EmailResultsTo = @email";
                cmd.Parameters.AddWithValue("@url", url);
                cmd.Parameters.AddWithValue("@email", email);

                var deleted = cmd.ExecuteNonQuery();

                return View(deleted);
            }
        }
    }
}