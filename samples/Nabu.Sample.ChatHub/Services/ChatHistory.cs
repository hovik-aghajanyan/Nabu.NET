using System;
using System.Collections.Generic;
using System.Linq;
using Nabu.Sample.ChatHub.Models;

namespace Nabu.Sample.ChatHub.Services
{
    /// <summary>The chat room's in-memory message store.</summary>
    public sealed class ChatHistory
    {
        private readonly object _sync = new object();
        private readonly List<ChatMessage> _messages = new List<ChatMessage>();

        public ChatMessage Add(string user, string text)
        {
            var message = new ChatMessage(Guid.NewGuid(), user, text, DateTimeOffset.UtcNow);
            lock (_sync)
            {
                _messages.Add(message);
                if (_messages.Count > 500)
                {
                    _messages.RemoveAt(0);
                }
            }

            return message;
        }

        public IReadOnlyList<ChatMessage> GetRecent(int count)
        {
            lock (_sync)
            {
                return _messages.Skip(Math.Max(0, _messages.Count - count)).ToList();
            }
        }

        public bool Delete(Guid id)
        {
            lock (_sync)
            {
                return _messages.RemoveAll(message => message.Id == id) > 0;
            }
        }
    }
}
