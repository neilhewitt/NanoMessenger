using System;
using System.Net;
using NUnit.Framework;

namespace NanoMessenger.Tests
{
    [TestFixture, Explicit, Timeout(10000)]
    public class ConnectionTests
    {
        public Messenger MakeTransmitter(int retries = Messenger.DEFAULT_MAX_RETRIES, int connectTimeout = Messenger.DEFAULT_CONNECTION_TIMEOUT)
        {
            Messenger transmitter = Messenger.Transmitter("Server", "127.0.0.1", 16384);
            transmitter.MaxConnectionRetries = retries;
            transmitter.ConnectTimeoutInMilliseconds = connectTimeout;
            transmitter.PingEnabled = false;
            return transmitter;
        }

        public Messenger MakeReceiver(int timeout = Messenger.DEFAULT_CONNECTION_TIMEOUT)
        {
            Messenger receiver = Messenger.Receiver("Server", 16384);
            receiver.ListenTimeoutInMilliseconds = timeout;
            receiver.PingEnabled = false;
            return receiver;
        }

        [Test]
        public void GivenNoResponseFromReceiver_WhenMaxRetriesSet_TransmitterConnectionFailsAndIsClosed()
        {
            using (Messenger server = MakeTransmitter(retries: 1, connectTimeout: 1))
            {
                bool waiting = true;
                server.OnConnectionRetriesExceeded += (sender, e) => { 
                    waiting = false; 
                    Assert.That(server.Connected == false && server.Closed == true); 
                };
                
                server.BeginConnect();

                while (waiting && !server.Connected) ;
            }
        }

        [Test]
        public void GivenNoConnectionFromTransmitter_WhenTimeoutIsSetAndExceeded_ReceiverConnectionFailsAndIsClosed()
        {
            using (Messenger client = MakeReceiver(timeout: 1))
            {
                bool waiting = true;
                client.OnListenerTimedOut += (sender, e) => {
                    waiting = false;
                    Assert.That(client.Connected == false && client.Closed == true);
                };

                client.BeginConnect();

                while (waiting && !client.Connected) ;
            }
        }

        [Test]
        public void GivenLoopbackAddressSuppliedToTransmitterAndReceiver_TransmitterAndReceiverCanConnect()
        {
            using (Messenger server = MakeTransmitter())
            {
                using (Messenger client = MakeReceiver())
                {
                    bool retrying = false;
                    server.OnConnectionRetry += (sender, e) => retrying = true;

                    client.BeginConnect();
                    server.BeginConnect();

                    while ((!server.Connected || !client.Connected) && !retrying) ;

                    Assert.That(server.Connected && client.Connected);
                }
            }
        }
    }
}
