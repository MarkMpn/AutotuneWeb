using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;

namespace AutotuneWeb.Models
{
    public class NSProfile
    {
        public string DefaultProfile { get; set; }

        public IDictionary<string,NSProfileDetails> Store { get; set; }

        public string Units { get; set; }
    }

    public class NSProfileSwitch
    {
        [JsonProperty(PropertyName = "profile")]
        public string Name { get; set; }

        public string ProfileJson { get; set; }
    }

    public class NSProfileDetails
    {
        public string Name { get; set; }

        public decimal Dia { get; set; }

        public string TimeZone { get; set; }

        public NSValueWithTime[] CarbRatio { get; set; }

        [JsonProperty(PropertyName = "sens")]
        public NSValueWithTime[] Sensitivity { get; set; }

        public NSValueWithTime[] Basal { get; set; }
        
        public string Units { get; set; }

        public static NSProfileDetails LoadFromNightscout(Uri url)
        {
            NSProfileDetails profile = null;

            // Get the profile from NS
            // Try looking for a Profile Switch event first
            var profileSwitchUrl = new Uri(url, "/api/v1/treatments.json?find[eventType][$eq]=Profile%20Switch&count=1");
            var req = WebRequest.CreateHttp(profileSwitchUrl);
            using (var resp = req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                var json = reader.ReadToEnd();
                var profileSwitches = JsonConvert.DeserializeObject<NSProfileSwitch[]>(json);

                if (profileSwitches.Length == 1 && !String.IsNullOrEmpty(profileSwitches[0].ProfileJson))
                {
                    profile = JsonConvert.DeserializeObject<NSProfileDetails>(profileSwitches[0].ProfileJson);
                    profile.Name = profileSwitches[0].Name;

                    // Might get a profile but without a time zone - if so, store this profile but keep going to get
                    // the timezone later on.
                    if (!String.IsNullOrEmpty(profile.TimeZone))
                        return profile;
                }
            }

            // If there wasn't a profile switch, try again looking at the latest profile
            var profileUrl = new Uri(url, "/api/v1/profile/current");

            req = WebRequest.CreateHttp(profileUrl);
            using (var resp = req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                var json = reader.ReadToEnd();
                var profiles = JsonConvert.DeserializeObject<NSProfile>(json);

                var defaultProfile = profiles.Store[profiles.DefaultProfile];

                if (profile != null)
                {
                    profile.TimeZone = defaultProfile.TimeZone;
                    profile.Units = profile.Units ?? profiles.Units;
                    return profile;
                }

                defaultProfile.Name = profiles.DefaultProfile;
                defaultProfile.Units = defaultProfile.Units ?? profiles.Units;
                return defaultProfile;
            }
        }

        public OapsProfile MapToOaps()
        {
            var oaps = new OapsProfile
            {
                Min5mCarbImpact = 8,
                Dia = this.Dia,
                BasalProfile = this.Basal
                    .GroupBy(b => b.TimeAsSeconds) // Nightscout often ends up having multiple entries for the same
                    .Select(g => g.Last())         // time, dedupe and take the last entry for each time
                    .OrderBy(b => b.TimeAsSeconds)
                    .Select(b => new OapsBasalProfile
                    {
                        Minutes = b.TimeAsSeconds / 60,
                        Rate = b.Value
                    })
                    .ToArray(),
                IsfProfile = new OapsIsfProfile
                {
                    Sensitivities = this.Sensitivity
                        .Select((s, i) => new OapsSensitivity
                        {
                            Index = i,
                            Sensitivity = ToMgDl(s.Value),
                            Offset = s.TimeAsSeconds / 60,
                            X = i, // TODO: not sure what X is for
                            EndOffset = i == this.Sensitivity.Length - 1 ? 1440 : (this.Sensitivity[i-1].TimeAsSeconds / 60)
                        })
                        .ToArray()
                },
                CarbRatio = this.CarbRatio[0].Value
            };

            return oaps;
        }

        private decimal ToMgDl(decimal value)
        {
            if (this.Units?.ToLower().Contains("mmol") == true)
                return value * 18;

            return value;
        }
    }

    public class NSValueWithTime
    {
        private int? _timeAsSeconds;

        public string Time { get; set; }

        public decimal Value { get; set; }

        public int TimeAsSeconds
        {
            get
            {
                if (_timeAsSeconds != null)
                    return _timeAsSeconds.Value;

                return (int) TimeSpan.Parse(Time).TotalSeconds;
            }
            set
            {
                _timeAsSeconds = value;
            }
        }
    }
}