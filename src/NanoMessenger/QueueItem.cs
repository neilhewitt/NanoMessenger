using System;

namespace NanoMessenger
{
    public class QueueItem
    {
        public Message Message { get; private set; }
        public Action<string> Callback { get; private set; }

        public QueueItem(Message message, Action<string> callback = null)
        {
            Message = message;
            Callback = callback;
        }
    }
}
