using System;

namespace NanoMessenger
{
    public class Message
    {
        public static Message FromMessageString(string messageString)
        {
            string[] messageParts = messageString.Split('|');
            
            if (messageParts.Length < 3) ThrowInvalid();
            if (!Guid.TryParse(messageParts[0], out Guid id)) ThrowInvalid();
            if (!DateTime.TryParse(messageParts[1], out DateTime dateStamp)) ThrowInvalid();

            Message message = new Message(messageParts[2]);
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
            return $"{ ID.ToString() }|{ DateStamp.ToString() }|{ Text }";
        }

        public Message(string messageText)
        {
            Text = messageText;
        }
    }
}
