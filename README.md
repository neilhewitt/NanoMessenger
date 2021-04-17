# NanoMessenger

A tiny messaging library that's almost no library at all. Use the **Messenger** class at both ends of the connection to pass messages between two endpoints with an intermediate queue for disconnected scenarios. Queued messages are only delivered if the dropped end of the conversation resumes during the current session, and all queued messages will then be delivered *before* any new message is sent. 

Note: This is not MSMQ. Or MQSeries. Or a message queue at all. It's a simple TCP/IP pipe between two network endpoints that passes contextless messages in plain text with a queue that accumulates messages if the connection is down, and sends them if and when it comes up again. Message delivery is not guaranteed under any circumstances and messages, once sent, cannot be re-played. 

I use it for a number of client/server utilities that I wrote for my home cockpit project. If it's useful to you, by all means use it. 

*Note: the project targets .NET Standard 2.0 and will not be pushed beyond there as I need it to work with .NET Framework 4.x project as well as .NET Core / 5.0.*

TODO: a basic how-to, and some tests! I'll also publish to nuget.org eventually.

**NanoMessenger is licensed under the BSD 3-clause license. See LICENSE for details.**
