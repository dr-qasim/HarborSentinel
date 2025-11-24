using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.Models
{
    public sealed class FlaggedVerdict
    {
        public string? Label { get; init; }
        public double? Confidence { get; init; }
        public string? Reason { get; init; }
    }
}
