using System;

namespace NanoMessenger
{
    public class Message
    {
        public static Message Parse(string messageString)
        {
            string[] messageParts = messageString.Split('|');
            
            if (messageParts.Length < 3) ThrowInvalid();
            if (!Guid.TryParse(messageParts[0], out Guid id)) ThrowInvalid();
            if (!DateTime.TryParse(messageParts[1], out DateTime dateStamp)) ThrowInvalid();

            string messageText = messageParts[2];
            messageText = messageText.Replace($"\\{ Messenger.INTERNAL_MESSAGE_TOKEN }", Messenger.INTERNAL_MESSAGE_TOKEN);
            messageText = messageText.Replace(Messenger.PIPE_ESCAPE, "|");

            Message message = new Message(messageText);
            message.DateStamp = dateStamp;
            message.ID = id;

            return message;

            void ThrowInvalid()
            {
                throw new ArgumentException("Invalid message string format.");
            }
        }

        public DateTime DateStamp { get; private set; } = DateTime.UtcNow;
        public Guid ID { get; private set; } = Guid.NewGuid();
        public string Text { get; private set; }

        public override string ToString()
        {
            string messageText = Text;
            messageText = messageText.Replace(Messenger.INTERNAL_MESSAGE_TOKEN, $"\\{ Messenger.INTERNAL_MESSAGE_TOKEN }");
            messageText = messageText.Replace("|", Messenger.PIPE_ESCAPE);
            return $"{ ID.ToString() }|{ DateStamp.ToString() }|{ messageText }";
        }

        public Message(string messageText)
        {
            Text = messageText;
        }
    }
}
