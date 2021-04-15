# NanoMessenger

A tiny messaging library that's almost no queue at all. Use the *Messenger* class at both ends of the connection to pass messages between two endpoints with an intermediate queue for disconnected scenarios. Queued messages are only delivered if the dropped end of the conversation resumes during the current session, and all queued messages will then be delivered before any new message is sent. 

Note: This is not MSMQ. Or MQSeries. Or a message queue at all. It's a simple TCP/IP pipe between two network endpoints that passes messages in plain text with a queue that accumulates messages if the client endpoint or the connection is down, and sends them if and when it comes up again. 

If it's useful to you, by all means use it. 

*NanoMessenger is licensed under the BSD 3-clause license.*
