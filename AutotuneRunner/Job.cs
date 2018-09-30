using System;
using System.Collections.Generic;
using System.Text;

namespace AutotuneRunner
{
    class Job
    {
        public int JobID { get; set; }
        public string NSUrl { get; set; }
        public string Profile { get; set; }
        public decimal PumpBasalIncrement { get; set; }
        public string EmailResultsTo { get; set; }
        public string Units { get; internal set; }
        public bool UAMAsBasal { get; internal set; }
    }
}
