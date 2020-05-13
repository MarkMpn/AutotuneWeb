using System;
using Microsoft.Azure.Cosmos.Table;

namespace AutotuneWeb.Models
{
    public class Job : TableEntity
    {
        public string NSUrl { get; set; }
        public double PumpBasalIncrement { get; set; }
        public string EmailResultsTo { get; set; }
        public string Units { get; set; }
        public string TimeZone { get; set; }
        public bool UAMAsBasal { get; set; }
        public int Days { get; set; }
        public DateTime? ProcessingStarted { get; set; }
        public DateTime? ProcessingCompleted { get; set; }
        public string Result { get; set; }
        public bool Failed { get; set; }
    }
}
