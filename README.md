# NanoMessenger

A tiny messaging library that's almost no library at all. Use the **Messenger** class at both ends of the connection to pass messages between two endpoints with an intermediate queue for disconnected scenarios. Queued messages are only delivered if the dropped end of the conversation resumes during the current session, and all queued messages will then be delivered *before* any new message is sent. 

Note: This is not MSMQ. Or MQSeries. It's a simple TCP/IP pipe between two network endpoints that passes contextless messages in plain text with a queue that accumulates messages if the connection is down, and sends them if and when it comes up again. Message delivery is not guaranteed under any circumstances and messages, once sent, cannot be replayed. 

I use it for a number of client/server utilities that I wrote for my home cockpit project. If it's useful to you, by all means use it. 

*Note: the project targets .NET Standard 2.0 and will not be updated to .NET Standard 2.1, as I need it to work with .NET Framework 4.x projects as well as .NET Core / 5.0.*

**NanoMessenger is licensed under the BSD 3-clause license. See LICENSE for details.**  


## Getting started

#### Building from source

Once built, either add a dependency on the project in Visual Studio, or drop the output files into a folder and reference the DLL directly.

#### NuGet package

A current build of the library will always be available at https://www.nuget.org/packages/NanoMessenger. 

### The Messenger class

The only usable class in this library is the Messenger class. The constructors are private so you must use the static factory methods Messenger.Receiver() or Messenger.Transmitter(), for the client and server instances respectively.

Both Transmitters and Receivers are instances of the Messenger class. The only difference between a Receiver and a Transmitter is that a Transmitter actively attempts to connect to the destination IP address specified, whereas the Receiver will set up a TcpListener and wait to be connected. Once connected, you can pass messages in either direction. A Transmitter can only connect to a Receiver and a Receiver can only receive connections from a Transmitter, so they must come in pairs or else things will simply not work. 

#### Examples:

    Messenger server = Messenger.Transmitter("Nickname for connection", "CLIENT_PC_NAME", 16384);
  
You specify a nickname for the connection (this can be handy if you're running multiple connections) which can be any string. The transmitter needs the DNS name (or IP address as string) of the client PC to connect to. The third parameter is the port number to use (this must not be blocked by the Windows Firewall or any network policy, and unblocking it is your responsibility).

    Messenger client = Messenger.Receiver("Nickname for connection", 16384);
  
The receiver factory method needs no IP address because a receiver will accept a connection from any address on any interface, but otherwise the parameters are identical.

#### Pings

By default the ping function is always enabled, and pings are sent by each end of the connection every 3 seconds. If the ping is not responded to within 5 seconds with a ping back, then the connection is deemed to have dropped. When a connection drop happens, the Messenger on each end will tidy up and close down the connection and then begin trying to re-connect. You do not have to re-initialise the connection process as this always happens automatically when a connection drops.

However, there are circumstances where you might not want to have the pings enabled (for example if debugging) as they run on a separate thread and will not stop at the breakpoint. So you can set the PingEnabled property to false. You should not do this in a production context as otherwise the Messenger will go on attempting to send and receive messages until the underlying TcpClient becomes aware that it is disconnected (which may never happen).

You can set the ping timeout and interval between pings as properties on the Messenger class. You can also specify that more than one ping can fail before the connection is deemed to have dropped by setting the MaxAllowedFailedPings property. Generally, you should leave these settings at the defaults.

#### Opening and Closing the Messenger

Two methods, BeginConnect() and Close(), are supplied. BeginConnect() begins the process of attempting to connect / listening for a connection. Close() closes down the connection but does not dispose any threads or resources, and once closed, the connection can be re-established by calling BeginConnect() again. 

The Connected property indicates if the Messenger is currently connected. This status is controlled by the ping mechanism, so it's possible for a connection to have disconnected during the timeout period for the ping (or if pinging is disabled) while Connected remains true. You should account for this in your application.

When the connection is closed in an orderly manner (ie Close() has been called), a private message is sent to the other end of the connection signalling that it is about to close. This allows the other end to immediately disconnect and clean up rather than waiting for a ping to fail. When this happens, the OnDisconnected event will be fired at the remote end of the connection. There are OnClosing and OnClosed events, but these only fire when Close() is called directly by client code, so do not rely on these events to know when the connection has dropped.

#### Timeouts ####

You can set various timeouts on the connection operation. 

On the Transmitter, set the ConnectionTimeoutInSeconds property to the maximum time in seconds that the Transmitter should wait to connect on each attempt. The TcpClient will time out eventually, but this timeout period is potentially quite long, so you may wish to set a shorter timeout. Note that if the timeout is exceeded, the Transmitter will continue trying to connect; this timeout only affects the amount of time each connection attempt is sustained for. You can set the MaxConnectionRetries property to set a maximum number of connection retries, after which the Messenger will close and stop trying to connect until you call BeginConnect() again.

On the Receiver, set the ListenTimeoutInSeconds property to set a limit to how long the Receiver will wait to be connected to by a Transmitter. If this timeout is exceeded, the Listener will close and stop listening for connections until you call BeginConnect() again.

Events are fired when any of these limits is exceeded (see event table below).

#### Sending messages

The core of the Messenger is a queue to which messages can be added for transmission to the other end of the connection.

To add a message to the queue, call QueueMessage(). It takes a single string parameter containing the message to send. Messages may only be strings and are unstructured, though you can of course use your own format within the string to structure the data as you wish.

    Message message = messenger.QueueMessage("Hello, world!");
    
Normally, this message will be sent immediately. If the connection is down, it will sit in the queue. If you add messages at a faster rate than the Messenger can send them, then the queue size will grow and messages will be sent one at a time in a strictly first-in, first-out order.

The QueueMessage() method returns a Message object which is a reference to the object placed on the queue and includes the message text, a timestamp, and a GUID which uniquely identifies this message. If you need to track message delivery you will need to keep hold of this ID. 

#### Receiving messages

To receive incoming messages, you should bind to the OnReceiveMessage event. The event handler will supply the Message object for the message which includes its ID and timestamp (so you could measure delivery latency if you needed to).

Remember that messages can be sent in both directions (receiver to transmitter and transmitter to receiver), and so both ends can receive messages. The only difference between the two is that transmitters actively try to connect to an endpoint, whereas receivers wait to be connected **to**. Generally, message transmission for your application will be transmitter -> receiver (hence the names) but for your application it may well be necessary to send messages back to the transmitter and there is no difference in how this is done. 

When a message is received by either end, that end of the connection will send back an acknowledgement message. This is an *internal* message and you will **not** receive the OnReceiveMessage event when it arrives. If you need to verify that messages have been received you can subscribe to the OnReceiveAcknowledge event which will supply the message ID of the received message. 

#### Other events

The Messenger class has several other events you can subscribe to:

    OnConnecting                    sent when the Messenger begins trying to connect / starts listening for connections
    OnConnectionRetry               sent when the Messenger (Transmitter) has failed to connect and is about to retry
    OnConnected                     sent when the Messenger connects in either direction
    OnDisconnected                  sent when the Messenger drops the connection due to the other end being unavailable or signalling it has closed
    OnPing                          sent when the Messenger sends a PING message
    OnPingBack                      sent when a return is received for a PING
    OnClosing                       sent when Close() has been called, but before the connection has closed
    OnClosed                        sent when the Close() operation completes
    OnMessageLoopIOException        sent when an IOException occurs during the main message loop
    OnListenerTimedOut              sent when a timeout is set for the Receiver to be connected to, and that timeout is exceeded
    OnConnectionRetriesExceeded     sent when a limit is set for the number of times a Transmitter will attempt to connect, and that limit has been exceeded 

#### Disposing the Messenger

The Messenger uses several threads to ping and send / receive messages. When you call Close(), these threads are **not** terminated. Messenger implements IDisposable and you should either use the using() pattern or call Dispose() when you are done.

There is a finalizer which will call Dispose() if you forget to, but this only runs when the object is GCd and depending on your application this might not happen for a while, and this might keep your application open in the background. So DON'T FORGET TO DISPOSE!

## Sample code

Below is a simple program that opens a transmitter and receiver on the same machine (note the loopback address) and sends messages in one direction as fast as it can while displaying the incoming acknowledgements and pings / ping backs. If you want to see it actually progressing then just uncomment the Thread.Sleep line.

I'm acutely aware that having just told you to always dispose, this code does not call Dispose. But it's a forever loop which is only interrupted when you close the app, and as it's a console app everything will close and the finalizer will get called immediately. So, you know, no problem there :-)

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
                receiver.BeginConnect();

                transmitter.OnReceiveAcknowledge += (sender, id) => { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine($"Acknowledge: { id.ToString() }"); };
                transmitter.OnPing += (sender, e) => { Console.ForegroundColor = ConsoleColor.Blue; Console.WriteLine("Transmitter ping..."); };
                transmitter.OnPingBack += (sender, e) => { Console.ForegroundColor = ConsoleColor.Blue; Console.WriteLine("Transmitter pingback."); };
                transmitter.BeginConnect();

                while (true)
                {
                    if (receiver.Connected && transmitter.Connected)
                    {
                        transmitter.QueueMessage("This is a test message.");
                        //Thread.Sleep(500);
                    }
                }
            }
        }
    }
