# NanoMessenger

A tiny messaging library that's almost no library at all. Use the **Messenger** class at both ends of the connection to pass messages between two endpoints with an intermediate queue for disconnected scenarios. Queued messages are only delivered if the dropped end of the conversation resumes during the current session, and all queued messages will then be delivered *before* any new message is sent. 

Note: This is not MSMQ. Or MQSeries. It's a simple TCP/IP pipe between two network endpoints that passes contextless messages in plain text with a queue that accumulates messages if the connection is down, and sends them if and when it comes up again. Message delivery is not guaranteed under any circumstances and messages, once sent, cannot be replayed. 

I use it for a number of client/server utilities that I wrote for my home cockpit project. If it's useful to you, by all means use it. 

*Note: the project targets .NET Standard 2.0 and will not be updated to .NET Standard 2.1, as I need it to work with .NET Framework 4.x projects as well as .NET Core / 5.0.*

**NanoMessenger is licensed under the BSD 3-clause license. See LICENSE for details.**  


## Getting started

#### Building from source

Once built, either add a dependency on the project in Visual Studio, or drop the output files into a folder and reference the DLL directly. At present it's not possible to compile NanoMessenger into a single DLL.

#### If using a local NuGet package

The NanoMessenger project is configured to build a .nupkg file on each build. This is **not** build-revved. You'll find it in the bin/debug or bin/release folders. If you copy this into a local NuGet folder / feed, you can add it to your project as a dependency, and if you're not making any source changes this can be a static dependency. Check the Releases in GitHub to see if there's a current release of the package for you to use rather than cloning the source. I will release the package on nuget.org once I'm happy with the quality and I've gone to v1.0.

### The Messenger class

The only usable class in this library is the Messenger class. The constructors are private so you must use the static factory methods Messenger.Receiver() or Messenger.Transmitter(), for the client and server instances respectively.

The only difference between a Receiver and a Transmitter is that a Transmitter actively attempts to connect to the destination IP address specified, whereas the Receiver will set up a TcpListener and wait to be connected. Once connected, you can pass messages in either direction. 

#### Examples:

    Messenger server = Messenger.Transmitter("Nickname for connection", "CLIENT_PC_NAME", 16384, 10);
  
You specify a nickname for the connection (this can be handy if you're running multiple connections) which can be any string. The transmitter needs the DNS name (or IP address as string) of the client PC to connect to. The third parameter is the port number to use (this must not be blocked by the Windows Firewall and unblocking it is your responsibility), and the fourth is the timeout (in seconds) for the ping operation that checks whether the connection is still live.

    Messenger client = Messenger.Receiver("Nickname for connection", 16384, 10);
  
The receiver factory method needs no IP address but otherwise the parameters are identical.

#### Pings

By default the ping function is always enabled, and pings are sent by each end of the connection every 3 seconds (this value is not currently configurable, although the timeout for the ping response, after which the connection will be deemed to have dropped, needs to be specified). When a connection drop happens the Messenger on each end will tidy up and close down the connection and then begin trying to re-connect. 

However, there are circumstances where you might not want to have the pings enabled (for example if debugging) as they run on a separate thread and will not stop at the breakpoint. So you can set the PingEnabled property to false. You should not do this in a deployed context as otherwise the Messenger will go on attempting to send and receive messages until the underlying TcpClient becomes aware that it is disconnected (which may never happen).

#### Opening and Closing the Messenger

Two methods, Open() and Close(), are supplied. Open() begins the process of attempting to connect. Close() closes down the connection but does not dispose any threads or resources, and the connection can be re-opened by calling Open() again. 

The Connected property indicates if the Messenger is currently connected. This status is controlled by the ping mechanism, so it's possible for a connection to have disconnected during the timeout period for the ping (or if pinging is disabled) while Connected remains true. You should account for this latency in your application. 

#### Sending messages

The core of the Messenger is the queue to which messages can be added for transmission to the other end of the connection.

To add a message to the queue, call QueueMessage(). It takes a single string parameter containing the message to send. Messages may only be strings and are unstructured, though you can of course use your own format within the string to structure the data if you wish.

    Message message = messenger.QueueMessage("Hello, world!");
    
Normally, this message will be sent immediately. If the connection is down, it will sit in the queue. If you add messages at a faster rate than the Messenger can send them, then the queue size will grow and messages will be sent one at a time in a strictly first-in, first-out order.

The character sequence "$$" is an escape sequence for messages indicating (when placed at the start of the message) that this is a private system message. If you include this sequence in your message it will be escaped for transmission and then un-escaped at the other end.

Each message has the sequence "$$ENDS" appended to it prior to transmission; this is used as an end-of-message token. It is removed from the received message. 

The QueueMessage() method returns a Message object which is a reference to the object placed on the queue and includes the message text, a timestamp, and a GUID which uniquely identifies this message. If you need to track message delivery you will need to keep hold of this ID. 

#### Receiving messages

To receive incoming messages, you should bind to the OnReceiveMessage event. This will supply the Message object for the message which includes its ID and timestamp (so you could measure delivery latency if you needed to).

Remember that messages can be sent in both directions (receiver to transmitter and transmitter to receiver), and so both ends can receive messages. Generally, message transmission for your application will be transmitter -> receiver (hence the names) but for your application it may well be necessary to send messages back to the transmitter and there is no difference in how this is done. 

When a message is received by either end, that end of the connection will send back an acknowledgement message. This is an *internal* message and you will **not** receive the OnReceiveMessage event when it arrives. If you need to verify that messages have been received you can subscribe to the OnReceiveAcknowledge event which will supply the message ID of the received message. 

#### Other events

The Messenger class has several other events you can subscribe to:

    OnConnecting // sent when the Messenger begins trying to connect / starts listening for connections
    OnConnectionRetry // sent when the Messenger has failed to connect and is about to retry
    OnConnected // sent when the Messenger connects in either direction
    OnDisconnected // sent when the Messenger senses the other end of the connection has dropped
    OnPing // sent when the Messenger sends a PING message
    OnPingBack // sent when a return is received for a PING

#### Disposing the Messenger

The Messenger uses several threads to ping and send / receive messages. When you call Close(), these threads are **not** terminated. Messenger implements IDisposable and you should either use the using() pattern or call Dispose() manually when you are done with a Messenger object.

There is a finalizer which will call Dispose() if you forget to, but this only runs when the object is GCd and depending on your application this might not happen for a while, it might keep your application open in the background. So DON'T FORGET TO DISPOSE!

## Sample code

Below is a simple program that opens a transmitter and receiver on the same machine (note the loopback address) and sends messages in one direction as fast as it can while displaying the incoming acknowledgements and pings / ping backs. If you want to see it actually progressing then just uncomment the Thread.Sleep line.

I'm acutely aware that having just told you to DISPOSE!, this code does not call Dispose. But it's a forever loop which is interrupted when you close the app, and as it's a console app everything will close and the finalizer will get called immediately. So, you know, no problem there.

    using System;
    using System.Threading;
    using NanoMessenger;

    namespace MessengerTest
    {
        class Program
        {
            static void Main(string[] args)
            {
                Messenger receiver = Messenger.Receiver("Receive", 16384);
                Messenger transmitter = Messenger.Transmitter("Transmit", "127.0.0.1", 16384);

                receiver.OnReceiveMessage += (sender, message) => { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"Message: { message.Text }"); };
                receiver.OnPing += (sender, e) => { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("Receiver ping..."); };
                receiver.OnPingBack += (sender, e) => { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("Receiver pingback."); };
                receiver.Open();

                transmitter.OnReceiveAcknowledge += (sender, id) => { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"Acknowledge: { id.ToString() }"); };
                transmitter.OnPing += (sender, e) => { Console.ForegroundColor = ConsoleColor.Blue; Console.WriteLine("Transmitter ping..."); };
                transmitter.OnPingBack += (sender, e) => { Console.ForegroundColor = ConsoleColor.Blue; Console.WriteLine("Transmitter pingback."); };
                transmitter.Open();

                while (true)
                {
                    if (receiver.Connected && transmitter.Connected)
                    {
                        transmitter.QueueMessage("This is a test message.");
                        Thread.Sleep(500);
                    }
                }
            }
        }
    }
