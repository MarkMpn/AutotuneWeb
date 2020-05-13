using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos.Table;

namespace AutotuneWeb.Models
{
    public class Statistics : TableEntity
    {
        public int JobCount { get; set; }
    }
}
