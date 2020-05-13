using Microsoft.Azure.Cosmos.Table;

namespace AutotuneWeb.Models
{
    public class Settings : TableEntity
    {
        public string Value { get; set; }
    }
}
