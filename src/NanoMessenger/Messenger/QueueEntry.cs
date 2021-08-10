using System;

namespace NanoMessenger
{
    public class QueueEntry
    {
        public Message Message { get; private set; }
        public Action<string> Callback { get; private set; }

        public QueueEntry(Message message, Action<string> callback = null)
        {
            Message = message;
            Callback = callback;
        }
    }
}
