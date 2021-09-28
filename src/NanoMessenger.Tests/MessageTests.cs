using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NanoMessenger.Tests
{
    [TestFixture]
    public class MessageTests
    {
        public const string RESERVED_SEQUENCE_TEXT = "This test message contains the reserved sequences $$ and |.";
        public const string ESCAPED_SEQUENCE_TEXT = "This test message contains the escaped sequences \\$$ and $$PIPE.";

        [Test]
        public void GivenAMessage_WhenConvertedToWireFormat_FormatIsValid()
        {
            string text = "This is a test message.";
            Message message = new Message(text);
            string wireFormat = message.ToWireFormat();
            string[] splitWireFormat = wireFormat.Split('|');

            Assert.That(
                splitWireFormat.Length == 3 &&
                Guid.TryParse(splitWireFormat[0], out Guid ID) &&
                DateTime.TryParse(splitWireFormat[1], out DateTime timestamp) &&
                splitWireFormat[2] == text
                );
        }

        [Test]
        public void GivenMessageTextContainsReservedSequences_WhenWireFormatted_SequencesAreEscaped()
        {
            Message message = new Message(RESERVED_SEQUENCE_TEXT);
            string wireFormat = message.ToWireFormat();

            Assert.That(wireFormat.EndsWith(ESCAPED_SEQUENCE_TEXT));
        }

        [Test]
        public void GivenWireFormattedMessageContainsEscapedSequences_WhenParsed_ReservedSequencesAreRestored()
        {
            Message message = new Message(RESERVED_SEQUENCE_TEXT);
            string wireFormat = message.ToWireFormat();
            Message message2 = Message.FromWireFormat(wireFormat);

            Assert.That(message2.Text == message.Text);
        }
    }
}
