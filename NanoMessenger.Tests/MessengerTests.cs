using System;
using System.Net;
using NUnit.Framework;

namespace NanoMessenger.Tests
{
    [TestFixture]
    public class MessengerTests
    {
        public Messenger MakeTransmitter(int retries = 0) => Messenger.Transmitter("Server", "127.0.0.1", 16384, 10, 10, retries);
        public Messenger MakeReceiver(int timeout = 0) => Messenger.Receiver("Server", 16384, 10, 10, timeout);

        [Test]
        public void GivenNoResponseFromClient_WhenMaxRetriesSet_ConnectionFailsAndIsClosed()
        {
            using (Messenger server = MakeTransmitter(1))
            {
                bool waiting = true;
                server.OnConnectionRetriesExceeded += (sender, e) => { 
                    waiting = false; 
                    Assert.That(server.Connected == false && server.Closed == true); 
                };
                server.Open();

                while (waiting && !server.Connected) ;
            }
        }

        [Test]
        public void ServerConnectsToClient()
        {
            using (Messenger server = MakeTransmitter())
            {
                using (Messenger client = MakeReceiver())
                {
                    bool retrying = false;
                    server.OnConnectionRetry += (sender, e) => retrying = true;

                    client.Open();
                    server.Open();

                    while ((!server.Connected || !client.Connected) && !retrying) ;

                    Assert.That(server.Connected && client.Connected);
                }
            }
        }
    }
}
