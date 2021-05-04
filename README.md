# NanoMessenger

A tiny messaging library that's almost no library at all. Use the **Messenger** class at both ends of the connection to pass messages between two endpoints with an intermediate queue for disconnected scenarios. Queued messages are only delivered if the dropped end of the conversation resumes during the current session, and all queued messages will then be delivered *before* any new message is sent. 

Note: This is not MSMQ. Or MQSeries. Or a message queue at all. It's a simple TCP/IP pipe between two network endpoints that passes contextless messages in plain text with a queue that accumulates messages if the connection is down, and sends them if and when it comes up again. Message delivery is not guaranteed under any circumstances and messages, once sent, cannot be re-played. 

I use it for a number of client/server utilities that I wrote for my home cockpit project. If it's useful to you, by all means use it. 

*Note: the project targets .NET Standard 2.0 and will not be pushed to .NET Standard 2.1, as I need it to work with .NET Framework 4.x projects as well as .NET Core / 5.0.*

**NanoMessenger is licensed under the BSD 3-clause license. See LICENSE for details.**

## Getting started

### If building from source

Once built, either add a dependency on the project in Visual Studio, or drop the output files into a folder and reference the DLL directly. At present it's not possible to compile NanoMessenger into a single DLL.

### If using a local NuGet package

The NanoMessenger project is configured to build a .nupkg file on each build. This is **not** build-revved. You'll find it in the bin/debug or bin/release folders. If you copy this into a local NuGet folder / feed, you can add it to your project as a dependency, and if you're not making any source changes this can be a static dependency. Check the Releases in GitHub to see if there's a current release of the package for you to use rather than cloning the source. 

## The Messenger class

The only usable class in this library is the Messenger class. The constructors are private so you must use the static factory methods Messenger.Receiver() or Messenger.Transmitter(), for the client and server instances respectively.

The only difference between a Receiver and a Transmitter is that a Transmitter actively attempts to connect to the destination IP address specified, whereas the Receiver will set up a TcpListener and wait to be connected. Once connected, you can pass messages in either direction. 

Examples:

    Messenger server = Messenger.Transmitter("Nickname for connection", "CLIENT_PC_NAME", 16384, 10);
  
You specify a nickname for the connection (this can be handy if you're running multiple connections) which can be any string. The transmitter needs the DNS name (or IP address as string) of the client PC to connect to. The third parameter is the port number to use (this must not be blocked by the Windows Firewall and unblocking it is your responsibility), and the fourth is the timeout (in seconds) for the ping operation that checks whether the connection is still live.

    Messenger client = Messenger.Receiver("Nickname for connection", 16384, 10);
  
The receiver factory method needs no IP address but otherwise the parameters are identical.

### Pings

By default the ping function is always enabled, and pings are sent by each end of the connection every 3 seconds (this value is not currently configurable, although the timeout for the ping response, after which the connection will be deemed to have dropped, needs to be specified (see above). When a connection drop happens the Messenger on each end will tidy up and close down the connection and then begin trying to re-connect. 

However, there are circumstances where you might not want to have the pings enabled (for example if debugging) as they run on a separate thread and will not stop at the breakpoint. So you can set the PingEnabled property to false. You should not do this in a deployed context as otherwise the Messenger will go on attempting to send and receive messages until the underlying TcpClient becomes aware that it is disconnected (which may never happen).

### Opening and Closing the Messenger

Two methods, Open() and Close(), are supplied. Open() begins the process of attempting to connect. Close() closes down the connection but does not dispose any threads or resources, and the connection can be re-opened by calling Open() again. 

The Connected property indicates if the Messenger is currently connected, although this status is controlled solely by the ping mechanism and it's possible for a connection to be disconnected during the timeout period for the ping or if pinging is disabled.




