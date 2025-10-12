using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.Models
{
    public sealed class FlaggedChat
    {
        public long Id { get; init; }
        public string? Title { get; init; }
        public string? Username { get; init; }
    }
}
