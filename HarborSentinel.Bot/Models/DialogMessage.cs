using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HarborSentinel.Bot.Models
{
    public sealed class DialogMessage
    {
        public DateTimeOffset Timestamp { get; init; }
        public long ChatId { get; init; }
        public string? ChatTitle { get; init; }
        public long? UserId { get; init; }
        public string? Username { get; init; }
        public string? Text { get; init; }
    }
}
