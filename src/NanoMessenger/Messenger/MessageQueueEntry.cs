using System;

namespace NanoMessenger
{
    public class MessageQueueEntry
    {
        public Message Message { get; private set; }
        public Action<string> Callback { get; private set; }

        public MessageQueueEntry(Message message, Action<string> callback = null)
        {
            Message = message;
            Callback = callback;
        }
    }
}
