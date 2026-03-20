using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ComplianceCheck
{
    internal class ComplianceResult
    {
        public string Filename { get; set; }
        public string Title { get; set; }
        public bool Compliant { get; set; }
        public string Reason { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
    }
}
