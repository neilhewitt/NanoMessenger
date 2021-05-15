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
        [Test]
        public void GivenMessageTextContainingReservedSequences_WhenWireFormatted_SequencesAreEscaped()
        {
            Message message = new Message("This test message contains the reserved sequences $$ and |.");
            string wireFormat = message.ToWireFormat();

            Assert.That(wireFormat.EndsWith("This test message contains the reserved sequences \\$$ and $$PIPE."));
        }

        [Test]
        public void GivenWireFormattedMessageContainingEscapedSequences_WhenParsed_SequencesAreRestored()
        {
            Message message = new Message("This test message contains the reserved sequences $$ and |.");
            string wireFormat = message.ToWireFormat();
            Message message2 = Message.FromWireFormat(wireFormat);

            Assert.That(message2.Text == message.Text);
        }
    }
}
