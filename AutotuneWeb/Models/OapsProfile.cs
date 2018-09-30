using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace AutotuneWeb.Models
{
    public class OapsProfile
    {
        [JsonProperty("min_5m_carbimpact")]
        public decimal Min5mCarbImpact { get; set; }

        [JsonProperty("dia")]
        public decimal Dia { get; set; }

        [JsonProperty("basalprofile")]
        public OapsBasalProfile[] BasalProfile { get; set; }

        [JsonProperty("isfProfile")]
        public OapsIsfProfile IsfProfile { get; set; }

        [JsonProperty("carb_ratio")]
        public decimal CarbRatio { get; set; }

        [JsonProperty("autosens_max")]
        public double AutosensMax { get; set; } = 1.2;

        [JsonProperty("autosens_min")]
        public double AutosensMin { get; set; } = 0.7;

        [JsonProperty("curve")]
        public string InsulinCurve { get; set; } = "rapid-acting";
    }

    public class OapsBasalProfile
    {
        [JsonProperty("start")]
        public string Start => TimeSpan.FromMinutes(Minutes).ToString("hh\\:mm\\:ss");

        [JsonProperty("minutes")]
        public int Minutes { get; set; }

        [JsonProperty("rate")]
        public decimal Rate { get; set; }
    }

    public class OapsIsfProfile
    {
        [JsonProperty("sensitivities")]
        public OapsSensitivity[] Sensitivities { get; set; }
    }

    public class OapsSensitivity
    {
        [JsonProperty("i")]
        public int Index { get; set; }

        [JsonProperty("start")]
        public string Start => TimeSpan.FromMinutes(Offset).ToString("hh\\:mm\\:ss");

        [JsonProperty("sensitivity")]
        public decimal Sensitivity { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }

        [JsonProperty("x")]
        public int X { get; set; }

        [JsonProperty("endoffset")]
        public int EndOffset { get; set; }
    }
}