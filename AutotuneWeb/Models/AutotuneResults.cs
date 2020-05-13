using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AutotuneWeb.Models
{
    public class AutotuneResults
    {
        private AutotuneResults()
        {
        }

        public Job Job { get; set; }
        public decimal PumpISF { get; set; }
        public decimal AutotuneISF { get; set; }
        public decimal PumpCR { get; set; }
        public decimal AutotuneCR { get; set; }
        public decimal[] PumpBasals { get; set; }
        public decimal[] AutotuneBasals { get; set; }
        public decimal[] SuggestedBasals { get; set; }
        public int[] DaysMissed { get; set; }
        public string Commit { get; set; }

        public static AutotuneResults ParseResult(string result, Job job)
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
            output.Job = job;
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
                output.SuggestedBasals[hour] = Math.Round(output.AutotuneBasals[hour] / (decimal) job.PumpBasalIncrement) * (decimal) job.PumpBasalIncrement;
                output.DaysMissed[hour] = daysMissed[$"{hour:00}:00"];
            }

            // If we want to get results in mmol, convert now
            if (job.Units.ToLower().Contains("mmol"))
            {
                output.PumpISF /= 18;
                output.AutotuneISF /= 18;
            }

            return output;
        }
    }
}
