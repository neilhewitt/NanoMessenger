using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NanoMessenger.Tests
{
    [TestFixture]
    public class MessengerTests
    {
        [Test]
        public void WhenMessageIsAddedToQueue_PeekQueueReturnsMostRecentMessage()
        {
            Messenger messenger = Messenger.Transmitter("Test", "127.0.0.1", 16384);
            messenger.QueueMessage("First");
            messenger.QueueMessage("Second");

            Assert.That(messenger.PeekQueue().Message.Text == "First");
        }
    }
}
