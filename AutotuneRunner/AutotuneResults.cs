using System;
using System.Collections.Generic;
using System.Text;

namespace AutotuneRunner
{
    class AutotuneResults
    {
        public decimal PumpISF { get; set; }
        public decimal AutotuneISF { get; set; }
        public decimal PumpCR { get; set; }
        public decimal AutotuneCR { get; set; }
        public decimal[] PumpBasals { get; set; }
        public decimal[] AutotuneBasals { get; set; }
        public decimal[] SuggestedBasals { get; set; }
        public int[] DaysMissed { get; set; }
    }
}
