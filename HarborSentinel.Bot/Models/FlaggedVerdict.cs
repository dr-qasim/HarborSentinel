using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.Models
{
    public sealed class FlaggedVerdict
    {
        public string? Label { get; init; }     // напр. "extremism", "narcotics", ...
        public double? Confidence { get; init; }     // 0..1
        public string? Reason { get; init; }     // краткое пояснение
    }
}
