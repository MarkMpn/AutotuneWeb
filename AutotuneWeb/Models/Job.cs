using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Text;

namespace AutotuneWeb.Models
{
    public class Job
    {
        private Job()
        {
        }

        public int JobID { get; set; }
        public string NSUrl { get; set; }
        public string Profile { get; set; }
        public decimal PumpBasalIncrement { get; set; }
        public string EmailResultsTo { get; set; }
        public string Units { get; internal set; }
        public string TimeZone { get; internal set; }
        public bool UAMAsBasal { get; internal set; }
        public int Days { get; set; }

        public static Job Load(SqlConnection con, int id)
        {
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT NSUrl, Profile, PumpBasalIncrement, EmailResultsTo, Units, TimeZone, CategorizeUAMAsBasal, DaysDuration FROM Jobs WHERE JobID = @Id";
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    var i = 0;
                    var nsUrl = reader.GetString(i++);
                    var profile = reader.GetString(i++);
                    var pumpBasalIncrement = reader.GetDecimal(i++);
                    var email = reader.GetString(i++);
                    var units = reader.GetString(i++);
                    var timezone = reader.GetString(i++);
                    var uamAsBasal = reader.GetBoolean(i++);
                    var days = reader.GetInt32(i++);

                    return new Job
                    {
                        JobID = id,
                        NSUrl = nsUrl,
                        Profile = profile,
                        PumpBasalIncrement = pumpBasalIncrement,
                        EmailResultsTo = email,
                        Units = units,
                        TimeZone = timezone,
                        UAMAsBasal = uamAsBasal,
                        Days = days
                    };
                }
            }
        }
    }
}
